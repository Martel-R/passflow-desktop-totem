# PassFlow Totem Kiosk (Desktop C# .NET 8)

Aplicação desktop nativa em **C# (.NET 8 WPF)** desenvolvida para terminais de autoatendimento (Totens de emissão de senhas) do ecossistema **PassFlow**.

Projetada para **execução única e portátil (`PassFlowTotem.exe`)**, sem necessidade de instalador ou configuração complexa.

---

## 🚀 Principais Recursos

- **Pareamento Automático via PIN:** Ao iniciar desvinculado, gera um código PIN de 6 dígitos que pode ser autenticado diretamente no painel web (*PassFlow Admin ➔ Gerenciar Telas / Dispositivos ➔ Vincular Totem*).
- **Regras do Web em Tempo Real:** Carrega dinamicamente a identidade visual (organização e filial), categorias, serviços, subserviços e permissões de atendimento.
- **Emissão Completa com Lei de Prioridades:** Suporte a atendimento **Convencional (Normal)** e **Prioritário (Preferencial)** com base na Lei Federal nº 10.048/2000.
- **Impressão Térmica Silenciosa e Direta:** Imprime diretamente na impressora padrão do Windows (ou impressora selecionada) sem abrir caixas de diálogo, com renderização gráfica em alta definição (80mm ou 58mm).
- **Modelo do Construtor de Cupom:** Respeita todas as regras e formatações salvas no *Construtor de Cupom* do PassFlow (escalas da senha, divisores, cabeçalhos, QR Code da fila virtual e notas de rodapé).
- **QR Code Dinâmico da Fila Virtual:** Gera e imprime o QR Code que permite ao cliente acompanhar sua vez e responder a pesquisa de satisfação direto pelo celular.
- **Modo Kiosk Fullscreen Touch:** Interface imersiva, moderna e responsiva para telas touch screen com retorno automático ao início por inatividade (30s).
- **Painel de Administração Protegido:** Pressione `F12`, `Ctrl+Alt+S` ou toque 5 vezes sobre a logomarca para alterar o servidor, testar impressão ou desvincular o totem.

---

## 🛠️ Tecnologias Utilizadas

- **Linguagem:** C# 12 / .NET 8 (LTS)
- **Framework UI:** Windows Presentation Foundation (WPF)
- **Geração de QR Code:** QRCoder
- **Impressão Direta:** `System.Drawing.Common` / `PrintDocument` com `StandardPrintController`
- **Comunicação REST:** `HttpClient` e `System.Text.Json`

---

## 📦 Como Compilar

Para gerar o arquivo executável único e portátil (`PassFlowTotem.exe`):

```bash
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

O arquivo gerado em `publish/PassFlowTotem.exe` pode ser copiado para qualquer computador com Windows 10/11 e executado imediatamente com dois cliques!
