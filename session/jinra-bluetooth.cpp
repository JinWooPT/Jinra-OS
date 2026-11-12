#include <QApplication>
#include <QDBusArgument>
#include <QDBusConnection>
#include <QDBusContext>
#include <QDBusInterface>
#include <QDBusMetaType>
#include <QDBusPendingCallWatcher>
#include <QDBusReply>
#include <QDialog>
#include <QLabel>
#include <QListWidget>
#include <QPushButton>
#include <QTimer>
#include <QVBoxLayout>

#include "yuzu/jinra_console_dialog.h"

using Interfaces = QMap<QString, QVariantMap>;
using Devices = QMap<QDBusObjectPath, Interfaces>;

class Agent final : public QObject, protected QDBusContext {
    Q_OBJECT
    Q_CLASSINFO("D-Bus Interface", "org.bluez.Agent1")
public:
    using QObject::QObject;
    QString allowed;

public slots:
    void Release() {}
    void Cancel() {}
    void RequestConfirmation(const QDBusObjectPath& path, uint) { Authorize(path); }
    void RequestAuthorization(const QDBusObjectPath& path) { Authorize(path); }
    void AuthorizeService(const QDBusObjectPath& path, const QString&) { Authorize(path); }

private:
    void Authorize(const QDBusObjectPath& path) {
        if (path.path() != allowed) {
            sendErrorReply(QStringLiteral("org.bluez.Error.Rejected"), QStringLiteral("Select this controller first."));
        }
    }
};

int main(int argc, char** argv) {
    QApplication app(argc, argv);
    auto bus = QDBusConnection::systemBus();
    const QString service = QStringLiteral("org.bluez");
    const QString adapter_interface = QStringLiteral("org.bluez.Adapter1");
    const QString device_interface = QStringLiteral("org.bluez.Device1");
    const QDBusObjectPath agent_path(QStringLiteral("/org/jinra/BluetoothAgent"));
    qDBusRegisterMetaType<Interfaces>();
    qDBusRegisterMetaType<Devices>();
    QDBusInterface manager(service, QStringLiteral("/"), QStringLiteral("org.freedesktop.DBus.ObjectManager"), bus);
    QDBusInterface agent_manager(service, QStringLiteral("/org/bluez"), QStringLiteral("org.bluez.AgentManager1"), bus);
    manager.setTimeout(2000);
    agent_manager.setTimeout(2000);

    QDialog dialog;
    auto* layout = CreateJinraDialogLayout(dialog, QStringLiteral("Bluetooth"), 620);
    if (!IsJinraConsoleSession()) {
        dialog.resize(620, 420);
    }
    auto* instructions = new QLabel(QStringLiteral("Unplug the controller. Hold Create and PS until the light flashes, then select your DualSense below."));
    instructions->setWordWrap(true);
    layout->addWidget(instructions);
    auto* devices = new QListWidget;
    devices->setMinimumHeight(150);
    devices->setMaximumHeight(250);
    layout->addWidget(devices);
    auto* status = new QLabel(QStringLiteral("Searching for controllers…"));
    status->setWordWrap(true);
    layout->addWidget(status);
    auto* connect_button = new QPushButton(QStringLiteral("Connect controller"));
    connect_button->setObjectName(QStringLiteral("JinraPrimary"));
    auto* close_button = new QPushButton(QStringLiteral("Close"));
    auto* buttons = new QHBoxLayout;
    buttons->addStretch();
    buttons->addWidget(close_button);
    buttons->addWidget(connect_button);
    layout->addLayout(buttons);
    connect_button->setEnabled(false);
    QObject::connect(close_button, &QPushButton::clicked, &dialog, &QDialog::reject);
    QObject::connect(devices, &QListWidget::currentItemChanged, &dialog, [=](QListWidgetItem* item) {
        connect_button->setEnabled(item != nullptr && !item->data(Qt::UserRole + 2).toBool());
    });

    Agent agent;
    const bool registered = bus.registerObject(agent_path.path(), &agent, QDBusConnection::ExportAllSlots) &&
        agent_manager.call(QStringLiteral("RegisterAgent"), QVariant::fromValue(agent_path), QStringLiteral("NoInputNoOutput")).type() != QDBusMessage::ErrorMessage;
    QString adapter;
    QTimer scan;
    scan.setInterval(1000);
    const auto refresh = [&] {
        QDBusReply<Devices> reply = manager.call(QStringLiteral("GetManagedObjects"));
        if (!reply.isValid()) {
            status->setText(QStringLiteral("Bluetooth service is unavailable."));
            return;
        }
        const auto managed = reply.value();
        if (adapter.isEmpty()) {
            for (auto it = managed.cbegin(); it != managed.cend(); ++it) {
                if (!it.value().contains(adapter_interface)) continue;
                adapter = it.key().path();
                QDBusInterface control(service, adapter, adapter_interface, bus);
                control.setTimeout(2000);
                control.setProperty("Powered", true);
                const auto start = control.call(QStringLiteral("StartDiscovery"));
                if (start.type() == QDBusMessage::ErrorMessage) {
                    status->setText(QStringLiteral("Could not scan for Bluetooth devices. Check the PC’s Bluetooth adapter."));
                }
                break;
            }
            if (adapter.isEmpty()) status->setText(QStringLiteral("No Bluetooth adapter found."));
        }

        const auto* current = devices->currentItem();
        const QString selection = current ? current->data(Qt::UserRole).toString() : QString{};
        devices->clear();
        for (auto it = managed.cbegin(); it != managed.cend(); ++it) {
            const auto props = it.value().value(device_interface);
            const auto name = props.value(QStringLiteral("Name")).toString();
            if (!name.startsWith(QStringLiteral("DualSense"), Qt::CaseInsensitive)) continue;
            const bool connected = props.value(QStringLiteral("Connected")).toBool();
            const bool paired = props.value(QStringLiteral("Paired")).toBool();
            const auto address = props.value(QStringLiteral("Address")).toString();
            auto* item = new QListWidgetItem(name + QStringLiteral(" · ") + address.right(5) +
                                             (connected ? QStringLiteral(" · Connected") : QString{}), devices);
            item->setData(Qt::UserRole, it.key().path());
            item->setData(Qt::UserRole + 1, paired);
            item->setData(Qt::UserRole + 2, connected);
            if (it.key().path() == selection) devices->setCurrentItem(item);
        }
        if (devices->count() == 1 && !devices->currentItem()) devices->setCurrentRow(0);
    };
    QObject::connect(&scan, &QTimer::timeout, &dialog, refresh);
    QObject::connect(connect_button, &QPushButton::clicked, &dialog, [&] {
        const auto* selected = devices->currentItem();
        if (!selected) return;
        const QString path = selected->data(Qt::UserRole).toString();
        const bool paired = selected->data(Qt::UserRole + 1).toBool();
        if (!paired && !registered) {
            status->setText(QStringLiteral("Could not register the Bluetooth pairing agent."));
            return;
        }
        agent.allowed = path;
        connect_button->setEnabled(false);
        devices->setEnabled(false);
        scan.stop();
        status->setText(QStringLiteral("Connecting…"));
        auto* device = new QDBusInterface(service, path, device_interface, bus, &dialog);
        device->setTimeout(22000);
        const auto connect = [&, device] {
            const bool trusted = device->setProperty("Trusted", true);
            auto* watcher = new QDBusPendingCallWatcher(device->asyncCall(QStringLiteral("Connect")), device);
            QObject::connect(watcher, &QDBusPendingCallWatcher::finished, &dialog, [&, device, trusted](auto* finished) {
                const auto error = finished->error();
                const bool connected = !finished->isError() || error.name() == QStringLiteral("org.bluez.Error.AlreadyConnected");
                const bool remembered = (trusted || device->property("Trusted").toBool()) &&
                                        device->property("Paired").toBool();
                if (connected && remembered) {
                    status->setText(QStringLiteral("Controller connected and remembered. Press PS to reconnect next time."));
                } else if (connected) {
                    status->setText(QStringLiteral("Controller connected, but Bluetooth could not confirm that it will reconnect automatically."));
                } else {
                    status->setText(QStringLiteral("Connection failed. Wake the controller and retry."));
                }
                agent.allowed.clear();
                device->deleteLater();
                devices->setEnabled(true);
                refresh();
                scan.start();
            });
        };
        if (paired) {
            connect();
            return;
        }
        auto* watcher = new QDBusPendingCallWatcher(device->asyncCall(QStringLiteral("Pair")), device);
        QObject::connect(watcher, &QDBusPendingCallWatcher::finished, &dialog, [&, device, connect](auto* finished) {
            if (!finished->isError()) {
                connect();
                finished->deleteLater();
                return;
            }
            status->setText(QStringLiteral("Pairing failed. Hold Create and PS again, then retry."));
            agent.allowed.clear();
            device->deleteLater();
            devices->setEnabled(true);
            refresh();
            scan.start();
        });
    });
    scan.start();
    const int result = dialog.exec();
    if (!adapter.isEmpty()) {
        QDBusInterface control(service, adapter, adapter_interface, bus);
        control.setTimeout(2000);
        control.call(QStringLiteral("StopDiscovery"));
    }
    if (registered) agent_manager.call(QStringLiteral("UnregisterAgent"), QVariant::fromValue(agent_path));
    return result == QDialog::Accepted ? 0 : 1;
}

#include "jinra-bluetooth.moc"
