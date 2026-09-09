# ⚡ AI Process Monitor `v1.2.0`

Monitor de processos e ferramentas de Inteligência Artificial em tempo real para Windows com interface nativa de desktop, detecção precisa de pausas no terminal para autorização humana e API HTTP local.

---

## 🚀 Funcionalidades

* **Controle de Versões Visível na Tela:** Versão `v1.2.0` exibida no título da janela, no badge do cabeçalho, na barra de status e nas respostas da API.
* **Detecção Inteligente de Pausas no Terminal (Ação Humana):** Detecta instantaneamente quando um agente CLI (como o Google Antigravity `agy`) entra em pausa no terminal aguardando autorização humana para executar comandos (`run_command`), criar/editar arquivos ou responder a perguntas (`ask_question`). Exibe no painel o motivo exato (ex: *"Aguardando autorização: Liberar porta 3000"*).
* **Interface Nativa Windows (Sem Navegador):** Aplicativo desktop nativo com tema escuro (Dark Mode), cartões de métricas, alertas vermelhos dinâmicos e controle de taxa de atualização.
* **Foco de Janela em 1 Clique:** Clique em qualquer linha ou no botão *"🎯 Abrir Janela"* para trazer o terminal ou janela correspondente para a frente da tela.
* **Bandeja do Sistema (System Tray):** Notificações balão no Windows quando uma IA pedir autorização humana.
* **API REST Nativa:** Endpoints JSON `/api/processes`, `/api/health` e `/api/focus`.

---

## 📦 Como Usar o Executável

1. **Modo Janela Nativa (Padrão):**
   * Dê dois cliques em **`AIProcessMonitor.exe`**.
   * Uma janela nativa de desktop Windows abrirá diretamente na sua tela com tema escuro moderno, métricas em tempo real, alertas de ação humana e botões para focar janelas (**sem precisar de navegador!**).
   * Se desejar visualizar pelo navegador, basta clicar no botão **"🌐 Abrir no Navegador"** no topo da janela.

2. **Modo Headless / Servidor em Segundo Plano:**
   ```powershell
   .\AIProcessMonitor.exe --headless
   ```

*(Opcional: você pode passar uma porta personalizada como argumento: `AIProcessMonitor.exe 4000`)*

---

## 📡 Endpoints da API

* `GET /api/processes`: Retorna o JSON com a lista completa de processos de IA, tempo ativo, consumo de RAM (MB), linha de comando e status de atenção humana.
* `GET /api/focus?pid={pid}`: Traz a janela do processo especificado para a frente da tela.
* `GET /api/health`: Healthcheck da API.

---

## 🛠️ Compilação

Para recompilar o executável a qualquer momento:
* Execute o arquivo `build.bat` ou rode no PowerShell:
  ```powershell
  & "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /target:winexe /out:AIProcessMonitor.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.Web.Extensions.dll /resource:public\index.html,index.html Program.cs
  ```

---

## 📁 Estrutura do Projeto

```
C:\Users\Julian\Dev\AIProcessMonitor\
├── AIProcessMonitor.exe   # Executável nativo standalone (GUI Windows nativa + API)
├── Program.cs             # Código-fonte em C# (.NET Framework 4.0)
├── build.bat              # Script de compilação em 1 clique
├── server.mjs             # Servidor alternativo em Node.js
├── scanner.ps1            # Script de inspeção de processos
├── public/
│   └── index.html         # Frontend web integrado (HTML, CSS e JavaScript)
└── README.md
```
