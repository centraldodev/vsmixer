# Arquitetura

## Camadas

- `Views` contém janelas, diálogos e integração com recursos nativos de seleção de arquivos.
- `ViewModels` mantém o estado apresentacional e os comandos.
- `Services/BassAudioEngine` encapsula os handles e operações nativas de áudio.
- `Services/ProjectFileService` implementa leitura, migração, caminhos relativos e gravação atômica.
- `Services/ProjectRecoveryService` mantém snapshots periódicos de projetos alterados.
- `Services/MetronomeScheduler` agenda clicks fora da thread da interface usando relógio monotônico.
- `Services/BeatGridCalculator` converte posição, BPM decimal e offset do primeiro beat em compasso/beat/tick.
- `Services/AppLogger` grava falhas em `%LOCALAPPDATA%/VSMixer/Logs` ou equivalente no sistema.

## Regras de ciclo de vida

- `App` é dono do `MainWindowViewModel` e o descarta no encerramento.
- O ViewModel descarta timers, scheduler, MIDI, processo e motor de áudio.
- O motor libera streams, samples e a instância BASS.
- Operações demoradas não devem atualizar coleções Avalonia fora da thread da interface.

## Formato de projeto

O campo `SchemaVersion` permite rejeitar formatos futuros e introduzir migrações. Tracks próximas ao projeto são salvas com caminhos relativos; caminhos de volumes diferentes permanecem absolutos.

## Áudio

As tracks atuais são vinculadas a um canal líder por `BASS_ChannelSetLink`, garantindo início e pausa simultâneos no macOS e reduzindo o intervalo no Windows. A evolução para precisão sample-accurate em todas as plataformas requer BASSmix nativo e um único mixer de saída.

A análise de tempo prioriza stems rítmicos, combina BPM/beat tracking do BASS_FX com um detector de transientes de fallback e preserva casas decimais. Como a detecção automática não consegue garantir qual beat musical é o downbeat em todos os arranjos, a tela de áudio permite marcar manualmente o compasso `1.1` durante a reprodução e ajustar a grade em passos de 10 ms.
