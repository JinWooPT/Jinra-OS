// SPDX-License-Identifier: GPL-3.0-or-later

#include <glib-unix.h>
#include <gst/app/gstappsrc.h>
#include <pipewire/pipewire.h>
#include <spa/param/audio/format-utils.h>
#include <spa/pod/builder.h>
#include <limits.h>
#include <signal.h>
#include <stdio.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

#include "jinra_capture_buffer.h"

enum { AudioRate = 48000, AudioChannels = 2, AudioFrameBytes = 8, VideoRate = 60 };
static const char ready_message[] = "JINRA_RECORDING_READY";

struct recorder {
    GMainLoop *loop;
    struct pw_loop *pipewire;
    struct pw_stream *audio_stream;
    GstElement *pipeline;
    GstAppSrc *video;
    GstAppSrc *audio;
    GstElement *rate;
    struct JinraCaptureBuffer *frames;
    uint64_t first_frame;
    uint64_t last_frame;
    const char *nvenc;
    guint bus_watch;
    const char *output;
    gint64 started;
    gint64 last_video;
    gint64 last_audio;
    bool audio_seen;
    bool audio_closed;
    bool cuda_upload;
    bool ready;
    bool stopping;
    bool saved;
};

static void finish_recording(struct recorder *r)
{
    if (r->stopping)
        return;
    r->stopping = true;
    if (!r->pipeline) {
        g_main_loop_quit(r->loop);
        return;
    }
    gst_app_src_end_of_stream(r->video);
    if (r->audio && !r->audio_closed)
        gst_app_src_end_of_stream(r->audio);
}

static void clear_pipeline(struct recorder *r)
{
    if (r->bus_watch) {
        g_source_remove(r->bus_watch);
        r->bus_watch = 0;
    }
    if (!r->pipeline)
        return;
    gst_element_set_state(r->pipeline, GST_STATE_NULL);
    g_clear_pointer(&r->video, gst_object_unref);
    g_clear_pointer(&r->audio, gst_object_unref);
    g_clear_pointer(&r->rate, gst_object_unref);
    g_clear_pointer(&r->pipeline, gst_object_unref);
}

static gboolean check_output(gpointer data);

static bool try_next_encoder(struct recorder *r)
{
    if (r->cuda_upload) {
        r->cuda_upload = false;
        g_printerr("Retrying NVENC with system-memory input.\n");
    } else if (r->nvenc) {
        r->nvenc = NULL;
        g_printerr("Retrying with the CPU encoder at 4K60.\n");
    } else {
        return false;
    }
    r->first_frame = 0;
    r->last_video = 0;
    return true;
}

static gboolean on_bus_message(GstBus *bus, GstMessage *message, gpointer data)
{
    struct recorder *r = data;
    (void)bus;
    switch (GST_MESSAGE_TYPE(message)) {
    case GST_MESSAGE_EOS: {
        check_output(r);
        guint64 in = 0, out = 0, duplicate = 0, dropped = 0, encoder_drops = 0;
        g_object_get(r->rate, "in", &in, "out", &out, "duplicate", &duplicate,
                     "drop", &dropped, NULL);
        if (g_object_class_find_property(G_OBJECT_GET_CLASS(r->video), "dropped"))
            g_object_get(r->video, "dropped", &encoder_drops, NULL);
        g_printerr("Recording frames: %" G_GUINT64_FORMAT " input, %" G_GUINT64_FORMAT
                   " output, %" G_GUINT64_FORMAT " repeated, %" G_GUINT64_FORMAT
                   " adjusted out, %" G_GUINT64_FORMAT " dropped in encoder queue, %"
                   G_GUINT64_FORMAT " missed in capture (%s).\n",
                   in, out, duplicate, dropped, encoder_drops,
                   __atomic_load_n(&r->frames->missed_frames, __ATOMIC_RELAXED),
                   r->nvenc ? "NVENC" : "x264");
        r->saved = r->ready;
        g_main_loop_quit(r->loop);
        break;
    }
    case GST_MESSAGE_ERROR: {
        GError *error = NULL;
        gchar *details = NULL;
        gst_message_parse_error(message, &error, &details);
        g_printerr("Encoder error from %s: %s (%s)\n", GST_OBJECT_NAME(message->src),
                   error->message, details ? details : "no details");
        g_clear_error(&error);
        g_free(details);
        if (!r->ready && !r->stopping && try_next_encoder(r)) {
            r->bus_watch = 0;
            clear_pipeline(r);
            return G_SOURCE_REMOVE;
        }
        g_main_loop_quit(r->loop);
        break;
    }
    default:
        break;
    }
    return G_SOURCE_CONTINUE;
}

static bool start_pipeline(struct recorder *r)
{
    gchar *encoder = r->nvenc ?
        g_strdup_printf("%s preset=p1 bitrate=60000 bframes=0 rc-lookahead=0 zerolatency=true",
                        r->nvenc) :
        g_strdup("videoconvert ! video/x-raw,format=NV12 ! "
                 "x264enc speed-preset=ultrafast tune=zerolatency threads=4 bitrate=60000");
    const bool audio = r->audio_seen && !r->audio_closed;
    gchar *description = g_strdup_printf(
        "matroskamux name=mux ! filesink name=output buffer-mode=unbuffered "
        "appsrc name=video is-live=true format=time max-buffers=2 "
        "max-bytes=0 leaky-type=downstream ! %s videorate name=rate ! "
        "video/x-raw%s,framerate=%u/1 ! %s ! h264parse ! queue ! mux.video_0 %s",
        r->cuda_upload ? "cudaupload ! queue max-size-buffers=3 max-size-bytes=0 max-size-time=0 ! " : "",
        r->cuda_upload ? "(memory:CUDAMemory)" : "", VideoRate, encoder, audio ?
        "appsrc name=audio is-live=true format=time do-timestamp=true max-buffers=32 "
        "max-bytes=0 leaky-type=downstream ! audioconvert ! opusenc bitrate=128000 ! "
        "queue ! mux.audio_0" : "");
    g_free(encoder);
    GError *error = NULL;
    r->pipeline = gst_parse_launch(description, &error);
    g_free(description);
    if (error) {
        g_printerr("Could not set up the encoder: %s\n", error->message);
        g_clear_error(&error);
        clear_pipeline(r);
        return false;
    }
    GstElement *output = gst_bin_get_by_name(GST_BIN(r->pipeline), "output");
    g_object_set(output, "location", r->output, NULL);
    gst_object_unref(output);
    r->video = GST_APP_SRC(gst_bin_get_by_name(GST_BIN(r->pipeline), "video"));
    r->rate = gst_bin_get_by_name(GST_BIN(r->pipeline), "rate");
    GstCaps *caps = gst_caps_new_simple("video/x-raw", "format", G_TYPE_STRING, "BGRx",
                                       "width", G_TYPE_INT, JinraCaptureWidth,
                                       "height", G_TYPE_INT, JinraCaptureHeight,
                                       "pixel-aspect-ratio", GST_TYPE_FRACTION, 1, 1,
                                       "framerate", GST_TYPE_FRACTION, 0, 1, NULL);
    gst_app_src_set_caps(r->video, caps);
    gst_caps_unref(caps);
    if (audio) {
        r->audio = GST_APP_SRC(gst_bin_get_by_name(GST_BIN(r->pipeline), "audio"));
        caps = gst_caps_from_string("audio/x-raw,format=F32LE,layout=interleaved,rate=48000,channels=2");
        gst_app_src_set_caps(r->audio, caps);
        gst_caps_unref(caps);
    }
    GstBus *bus = gst_element_get_bus(r->pipeline);
    r->bus_watch = gst_bus_add_watch(bus, on_bus_message, r);
    gst_object_unref(bus);
    g_printerr("Encoding %ux%u at %u FPS with %s%s, %s.\n", JinraCaptureWidth, JinraCaptureHeight,
               VideoRate, r->nvenc ? r->nvenc : "x264 (CPU)",
               r->cuda_upload ? " and CUDA upload" : "", audio ? "with audio" : "video only");
    if (gst_element_set_state(r->pipeline, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE) {
        clear_pipeline(r);
        return false;
    }
    return true;
}

static void release_slot(gpointer data)
{
    struct JinraCaptureSlot *slot = data;
    __atomic_store_n(&slot->state, JinraCaptureFree, __ATOMIC_RELEASE);
}

static void encode_video(struct recorder *r, struct JinraCaptureSlot *slot)
{
    const gint64 now = g_get_monotonic_time();
    if (slot->timestamp_ns <= r->last_frame)
        goto drop;
    if (!r->pipeline && !start_pipeline(r)) {
        if (!try_next_encoder(r))
            g_main_loop_quit(r->loop);
        goto drop;
    }
    GstBuffer *frame = gst_buffer_new_wrapped_full(GST_MEMORY_FLAG_READONLY, slot->pixels,
        sizeof(slot->pixels), 0, sizeof(slot->pixels), slot, release_slot);
    if (!frame)
        goto drop;
    if (!r->first_frame)
        r->first_frame = slot->timestamp_ns;
    r->last_frame = slot->timestamp_ns;
    GST_BUFFER_PTS(frame) = r->last_frame - r->first_frame;
    GST_BUFFER_DURATION(frame) = GST_SECOND / VideoRate;
    if (gst_app_src_push_buffer(r->video, frame) == GST_FLOW_OK)
        r->last_video = now;
    return;
drop:
    release_slot(slot);
}

static gboolean consume_video(gpointer data)
{
    struct recorder *r = data;
    if (r->stopping || (!r->audio_seen && !r->audio_closed &&
                        g_get_monotonic_time() - r->started < 250 * G_TIME_SPAN_MILLISECOND))
        return G_SOURCE_CONTINUE;
    for (int count = 0; count < JinraCaptureSlots; ++count) {
        struct JinraCaptureSlot *oldest = NULL;
        for (int i = 0; i < JinraCaptureSlots; ++i) {
            struct JinraCaptureSlot *slot = &r->frames->entries[i];
            if (__atomic_load_n(&slot->state, __ATOMIC_ACQUIRE) == JinraCaptureReady &&
                (!oldest || slot->timestamp_ns < oldest->timestamp_ns))
                oldest = slot;
        }
        if (!oldest)
            break;
        __atomic_store_n(&oldest->state, JinraCaptureEncoding, __ATOMIC_RELAXED);
        encode_video(r, oldest);
    }
    return G_SOURCE_CONTINUE;
}

static gboolean recording_control(gint fd, GIOCondition condition, gpointer data)
{
    struct recorder *r = data;
    (void)fd;
    (void)condition;
    consume_video(r);
    finish_recording(r);
    return G_SOURCE_REMOVE;
}

static void read_audio(void *data)
{
    struct recorder *r = data;
    struct pw_buffer *sample = pw_stream_dequeue_buffer(r->audio_stream);
    if (!sample)
        return;
    if (!r->stopping && sample->buffer->n_datas == 1) {
        const struct spa_data *plane = &sample->buffer->datas[0];
        const struct spa_chunk *chunk = plane->chunk;
        if (plane->data && chunk && chunk->offset <= plane->maxsize &&
            chunk->size <= plane->maxsize - chunk->offset && chunk->size >= AudioFrameBytes &&
            chunk->size % AudioFrameBytes == 0) {
            r->audio_seen = true;
            r->last_audio = g_get_monotonic_time();
            if (r->audio) {
                GstBuffer *buffer = gst_buffer_new_memdup((guint8 *)plane->data + chunk->offset,
                                                        chunk->size);
                GST_BUFFER_DURATION(buffer) = gst_util_uint64_scale(
                    chunk->size / AudioFrameBytes, GST_SECOND, AudioRate);
                gst_app_src_push_buffer(r->audio, buffer);
            }
        }
    }
    pw_stream_queue_buffer(r->audio_stream, sample);
}

static void audio_state(void *data, enum pw_stream_state old, enum pw_stream_state state,
                        const char *error)
{
    struct recorder *r = data;
    (void)old;
    if (r->stopping || (state != PW_STREAM_STATE_ERROR && state != PW_STREAM_STATE_UNCONNECTED))
        return;
    g_printerr("Audio monitor disconnected%s%s.\n", error ? ": " : "", error ? error : "");
    r->audio_closed = true;
    if (r->audio)
        gst_app_src_end_of_stream(r->audio);
}

static struct pw_stream *open_audio(struct recorder *r, const char *target)
{
    static const struct pw_stream_events audio_events = {
        .version = PW_VERSION_STREAM_EVENTS, .state_changed = audio_state, .process = read_audio,
    };
    struct pw_properties *props = pw_properties_new(
        PW_KEY_MEDIA_TYPE, "Audio", PW_KEY_MEDIA_CATEGORY, "Capture",
        PW_KEY_TARGET_OBJECT, target, PW_KEY_NODE_DONT_RECONNECT, "true",
        PW_KEY_STREAM_CAPTURE_SINK, "true", NULL);
    struct pw_stream *stream = pw_stream_new_simple(r->pipewire, "Jinra Recorder", props,
                                                   &audio_events, r);
    if (!stream)
        return NULL;
    r->audio_stream = stream;
    uint8_t storage[256];
    struct spa_pod_builder b = SPA_POD_BUILDER_INIT(storage, sizeof(storage));
    struct spa_audio_info_raw info = {.format = SPA_AUDIO_FORMAT_F32_LE, .rate = AudioRate,
        .channels = AudioChannels, .position = {SPA_AUDIO_CHANNEL_FL, SPA_AUDIO_CHANNEL_FR}};
    const struct spa_pod *format = spa_format_audio_raw_build(&b, SPA_PARAM_EnumFormat, &info);
    if (pw_stream_connect(stream, PW_DIRECTION_INPUT, PW_ID_ANY,
            PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS, &format, 1) < 0) {
        pw_stream_destroy(stream);
        return NULL;
    }
    return stream;
}

static gboolean dispatch_pipewire(gint fd, GIOCondition condition, gpointer data)
{
    struct recorder *r = data;
    (void)fd;
    if ((condition & (G_IO_ERR | G_IO_HUP)) || pw_loop_iterate(r->pipewire, 0) < 0) {
        audio_state(r, PW_STREAM_STATE_STREAMING, PW_STREAM_STATE_ERROR,
                    "PipeWire audio connection failed");
        return G_SOURCE_REMOVE;
    }
    return G_SOURCE_CONTINUE;
}

static gboolean on_interrupt(gpointer data)
{
    finish_recording(data);
    return G_SOURCE_CONTINUE;
}

static gboolean check_output(gpointer data)
{
    struct recorder *r = data;
    const gint64 now = g_get_monotonic_time();
    if (r->audio && !r->audio_closed && !r->stopping &&
        now - r->last_audio > 2 * G_USEC_PER_SEC) {
        g_printerr("No recent audio samples. Continuing with video only.\n");
        r->audio_closed = true;
        gst_app_src_end_of_stream(r->audio);
    }
    if (!r->ready && r->last_video) {
        struct stat file;
        if (stat(r->output, &file) == 0 && file.st_size > 0) {
            r->ready = true;
            g_print("%s\n", ready_message);
            fflush(stdout);
        }
    }
    if (!r->ready && !r->stopping && now - r->started > 10 * G_USEC_PER_SEC) {
        g_printerr("Timed out waiting for the first encoded video frame.\n");
        g_main_loop_quit(r->loop);
    }
    return G_SOURCE_CONTINUE;
}

int main(int argc, char **argv)
{
    if (argc != 4)
        return 2;
    struct recorder r = {.output = argv[1]};
    char *end;
    const guint64 descriptor = g_ascii_strtoull(argv[2], &end, 10);
    if (!argv[2][0] || *end || descriptor > INT_MAX || descriptor <= STDERR_FILENO)
        return 2;
    const int fd = (int)descriptor;
    struct stat metadata;
    if (fstat(fd, &metadata) || metadata.st_size != (off_t)sizeof(struct JinraCaptureBuffer)) {
        g_printerr("The shared video buffer could not be opened.\n");
        close(fd);
        return 1;
    }
    r.frames = mmap(NULL, sizeof(struct JinraCaptureBuffer), PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    close(fd);
    if (r.frames == MAP_FAILED) {
        g_printerr("The shared video buffer could not be mapped.\n");
        return 1;
    }
    gst_init(NULL, NULL);
    pw_init(NULL, NULL);
    GstPlugin *nvcodec = gst_plugin_load_by_name("nvcodec");
    g_clear_pointer(&nvcodec, gst_object_unref);
    const char *encoders[] = {"nvh264enc", "nvautogpuh264enc"};
    for (size_t i = 0; i < G_N_ELEMENTS(encoders); ++i) {
        GstElementFactory *factory = gst_element_factory_find(encoders[i]);
        if (factory) {
            r.nvenc = encoders[i];
            gst_object_unref(factory);
            break;
        }
    }
    if (!r.nvenc)
        g_printerr("NVENC is unavailable. Using the CPU encoder at 4K60.\n");
    else {
        GstElementFactory *upload = gst_element_factory_find("cudaupload");
        r.cuda_upload = upload != NULL;
        g_clear_pointer(&upload, gst_object_unref);
    }

    r.loop = g_main_loop_new(NULL, false);
    if (argv[3][0]) {
        r.pipewire = pw_loop_new(NULL);
        if (r.pipewire) {
            pw_loop_enter(r.pipewire);
            r.audio_stream = open_audio(&r, argv[3]);
            if (r.audio_stream)
                g_unix_fd_add(pw_loop_get_fd(r.pipewire), G_IO_IN | G_IO_ERR | G_IO_HUP,
                              dispatch_pipewire, &r);
        }
    }
    r.started = g_get_monotonic_time();
    r.audio_closed = !r.audio_stream;
    g_unix_signal_add(SIGINT, on_interrupt, &r);
    g_unix_signal_add(SIGTERM, on_interrupt, &r);
    g_unix_fd_add(STDIN_FILENO, G_IO_IN | G_IO_ERR | G_IO_HUP, recording_control, &r);
    g_timeout_add(2, consume_video, &r);
    g_timeout_add(100, check_output, &r);
    g_main_loop_run(r.loop);
    r.stopping = true;
    clear_pipeline(&r);
    if (r.audio_stream)
        pw_stream_destroy(r.audio_stream);
    if (r.pipewire) {
        pw_loop_leave(r.pipewire);
        pw_loop_destroy(r.pipewire);
    }
    munmap(r.frames, sizeof(struct JinraCaptureBuffer));
    g_main_loop_unref(r.loop);
    pw_deinit();
    return r.saved ? 0 : 1;
}
