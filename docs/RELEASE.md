# Processo de release

1. Atualize `Version` em `VSMixer.csproj`.
2. Execute build, formatação, testes e auditoria de dependências.
3. Valide manualmente importação, play/pausa, seek, loop, MIDI e troca de saída com hardware real.
4. Publique usando o perfil da plataforma.
5. Assine o executável Windows com certificado de code signing.
6. Monte o bundle `.app`, assine com Developer ID e envie para notarização Apple.
7. Valide o instalador em uma máquina limpa e sem SDK .NET.

## Credenciais externas

Certificados, Apple Developer ID, senha/notary profile e licença comercial BASS não devem ser armazenados no repositório. Configure-os como secrets do provedor de CI.

Para criar o bundle macOS com nome e ícone corretos:

```bash
./scripts/package-macos.sh osx-x64
```

Se `APPLE_SIGN_IDENTITY` estiver definido, o script também executa `codesign` com hardened runtime.

## Apple Silicon

Não remova o bloqueio de `osx-arm64` até que `libbass.dylib` e `libbass_fx.dylib` em `Native/osx-arm64` sejam confirmadas por `lipo -info` como ARM64 e testadas em hardware Apple Silicon.
