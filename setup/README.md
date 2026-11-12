# Jinra OS Setup

A aplicação prepara um disco para arrancar em UEFI diretamente no Jinra Home.
O Setup abre em inglês. Podes mudar para português no seletor **Language**.

Descarrega o Setup para Windows e o ficheiro `jinra-os-payload.tar.zst` na [mesma release do Jinra OS](https://github.com/JinWooPT/Jinra-OS/releases/latest). Extrai o Setup e coloca o pacote do sistema na mesma pasta. Para atualizar um Jinra OS já instalado, abre **Power → System updates** no Jinra Home.

## Requisitos

- Windows 11 de 64 bits, acesso de administrador e **.NET 10 Desktop Runtime x64**.
- Uma distribuição **Ubuntu em WSL 2**. A app usa o WSL para construir a imagem e o Windows para gravar o disco, incluindo dispositivos USB de setores lógicos de 512 bytes.
- Cerca de **15 GB livres** para a preparação no disco onde estão o WSL e os temporários do Windows.
- Um disco dedicado. **32 GB** chegam para o sistema com a biblioteca noutro disco. Para uma partição de jogos de 40 GiB, usa um disco de **128 GB**.
- Mantém o executável, os scripts, o pacote `jinra-os-payload.tar.zst` e a respetiva soma SHA-256 na mesma pasta.

Se ainda não tens Ubuntu, abre o Terminal como administrador e executa `wsl --install -d Ubuntu`. No Ubuntu, instala as ferramentas de imagem uma vez.

```bash
sudo apt update
sudo apt install python3 zstd gdisk e2fsprogs dosfstools mtools exfatprogs
```

## Preparar o disco

1. Abre **Jinra OS Setup.exe**, seleciona o disco de destino e confirma a capacidade. A preparação substitui as partições do disco escolhido.
2. Para jogos que já estejam noutro disco, seleciona **Usar uma pasta de jogos noutro disco** e escolhe a pasta. A origem precisa de estar numa partição GPT em NTFS ou exFAT sem BitLocker. O Jinra OS monta-a em modo só de leitura. Também podes criar uma partição **JINRA_GAMES** acessível pelo Windows para colocar jogos, logs e gravações.
3. Escolhe **NVIDIA** para instalar com o driver NVIDIA 595 Open e a biblioteca Linux de DLSS ou **AMD** para instalar com Mesa RADV e o driver amdgpu do kernel. O perfil NVIDIA seleciona DLAA por defeito no Zelda. O perfil AMD seleciona FSR 3.1. O DLSS é experimental no Linux e o FSR 4 depende da integração Windows nesta build.
4. Seleciona **Igual ao Windows** para pedir ao monitor principal a resolução e frequência atualmente usadas no Windows. O Gamescope usa um modo compatível se a combinação pedida estiver indisponível. A frequência do monitor é independente dos FPS que o jogo consegue produzir.
5. Para migrar mods, ativa **Copiar os mods do Jinra para o Jinra OS** e escolhe a pasta `user/load` do Jinra no Windows. A app importa as pastas identificadas pelo Title ID para a pasta de mods do Jinra OS. Os mods compatíveis com a versão do jogo ficam disponíveis sem teres de os importar novamente.
6. Carrega em **Preparar disco**. Espera pela verificação da leitura e seleciona o disco pelo menu de arranque UEFI do PC.

O Jinra arranca em modo Home com Vulkan, escala 1×, modo Fast, 12 GB de memória emulada e Frame Generation desligado. A pré-compilação, o Shader Sync e a cache de pipelines Vulkan estão disponíveis. A configuração usa **Skip** para pipelines Switch 2 ainda por compilar, com um orçamento de 8 ms para o modo de espera limitado. O botão de energia dá acesso a **Restart** e **Shut down**. Usa **Alt+F4** durante o jogo para regressar ao Home. O menu **Controls** permite emparelhar um DualSense por Bluetooth e o sistema tenta voltar a ligá-lo nos arranques seguintes.

Coloca o teu `prod.keys` em `Setup/prod.keys` na pasta dos jogos ou na partição **JINRA_GAMES**. O Jinra OS importa-o ao arrancar. Para instalar o teu ZIP de firmware, usa **Install content → Install firmware ZIP**.

Reserva espaço na partição **JINRA_GAMES** para guardar vídeos e logs acessíveis pelo Windows. Durante o jogo, carrega em **F9** para começar ou terminar a gravação em **4K a 60 FPS**, com áudio. **F10** alterna o overlay de desempenho. Quando está visível, o overlay também fica no vídeo. A gravação usa NVENC quando disponível e o codificador por CPU como alternativa.

Se a preparação indicar uma ferramenta em falta, confirma que a distribuição escolhida é WSL 2 e que o comando de instalação acima foi executado nela. Se a gravação indicar que o disco está em uso, fecha aplicações que estejam a aceder às partições desse disco.
