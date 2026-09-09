# ⚡ AI Process Monitor

Monitor de processos e ferramentas de Inteligência Artificial em tempo real para Windows, com API HTTP local, detecção de necessidade de ação humana e interface web interativa.

![AI Process Monitor](public/index.html)

---

## 🚀 Funcionalidades

* **Monitoramento Universal de IA:** Detecta automaticamente ferramentas e processos de IA ativos:
  * Google Antigravity CLI (`agy`)
  * Google Gemini Code Assist (`cloudcode_cli`) no VS Code
  * Microsoft 365 Copilot (`M365Copilot.exe`)
  * Modelos locais e servidores de inferência (Ollama, LM Studio, vLLM, ComfyUI, etc.)
  * Scripts locais de IA (Python com PyTorch/Transformers, LangChain, servidores web locais com IA)
* **Alerta Vermelho de Ação Humana:** Alerta visual e sonoro quando um processo ou agente de IA precisa da resposta/ação do usuário (prompts de terminal, perguntas interativas ou caixas de diálogo).
* **Foco de Janela em 1 Clique:** Clique em qualquer linha ou botão na interface para trazer a janela da aplicação ou terminal correspondente diretamente para o primeiro plano.
* **Taxa de Atualização Configurável:** Alterne entre 1s, 2s, 3s, 5s, 10s ou defina qualquer intervalo personalizado em segundos no navegador, com opção de pausar e atualizar manualmente.
* **API REST Nativa:** Endpoints JSON para consulta por outras aplicações ou scripts (`/api/processes`, `/api/health`, `/api/focus`).
* **Executável Nativo Leve:** Compilado em C#/.NET Framework sem dependência de Node.js ou runtime externo.

---

## 📦 Como Usar o Executável

1. Dê dois cliques em **`AIProcessMonitor.exe`**.
2. O servidor iniciará e o seu navegador padrão abrirá automaticamente em:
   ```
   http://localhost:3333
   ```
3. Para encerrar, basta fechar a janela do terminal ou pressionar `Ctrl + C`.

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
  & "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /target:exe /out:AIProcessMonitor.exe /r:System.dll /r:System.Core.dll /r:System.Management.dll /r:System.Web.Extensions.dll /resource:public\index.html,index.html Program.cs
  ```

---

## 📁 Estrutura do Projeto

```
C:\Dev\AIProcessMonitor\
├── AIProcessMonitor.exe   # Executável nativo standalone
├── Program.cs             # Código-fonte em C# (.NET Framework)
├── build.bat              # Script de compilação em 1 clique
├── server.mjs             # Servidor alternativo em Node.js
├── scanner.ps1            # Script de inspeção de processos
├── public/
│   └── index.html         # Frontend moderno (HTML, CSS e JavaScript)
└── README.md
```
