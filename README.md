# VSMixer

Reprodutor multitrack para Windows e macOS, voltado a ensaios e apresentações ao vivo.

## Recursos

- Reprodução sincronizada de múltiplas tracks, com mute, solo e roteamento A/B.
- Alteração de BPM e pitch, sessões, loop, metrônomo, voz guia e pads contínuos.
- BPM decimal e grade de beats com offset automático/manual para manter contador, sessões e metrônomo alinhados ao áudio.
- Controle e mapeamento MIDI.
- Projetos portáveis `.vsmixer`, com caminhos relativos e versão de formato.
- Autosave de recuperação e proteção contra fechamento com alterações não salvas.
- Importação e análise de waveform/BPM em background.

## Atalhos

| Ação | Atalho |
| --- | --- |
| Play/Pausa | `Espaço` |
| Voltar ao início | `Home` |
| Parada de emergência | `Esc` |
| Salvar | `Ctrl+S` / `Cmd+S` |

## Desenvolvimento

Requer o SDK .NET 10.

```bash
dotnet restore VSMixer.slnx
dotnet run --project VSMixer.csproj
dotnet test VSMixer.slnx
dotnet format VSMixer.slnx --verify-no-changes
```

O CI executa formatação, build, testes e auditoria de pacotes no Windows e macOS.

## Publicação

```bash
dotnet publish VSMixer.csproj -p:PublishProfile=win-x64
dotnet publish VSMixer.csproj -p:PublishProfile=osx-x64
```

A publicação `osx-arm64` é intencionalmente bloqueada enquanto não existir
`Native/osx-arm64/libbass_fx.dylib` oficial com arquitetura ARM64. Isso impede a geração de um app incompatível para Apple Silicon.

Consulte [Arquitetura](docs/ARCHITECTURE.md) e [Processo de release](docs/RELEASE.md).

## Dependências nativas e licenças

ManagedBass é apenas o wrapper .NET. As bibliotecas nativas BASS/BASS_FX possuem termos próprios; confirme a licença comercial antes de distribuir ou vender o aplicativo. Os arquivos de áudio incluídos também devem possuir autorização explícita de distribuição.
