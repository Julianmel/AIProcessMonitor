# ⚡ AI Process Monitor `v1.7.0`

Monitor de processos e agentes de Inteligência Artificial em tempo real para Windows com interface nativa desktop de alta performance, painel web responsivo com suporte mobile na rede local (Wi-Fi/LAN), QR Code dinâmico, síntese de voz em português, histórico de auditoria e webhooks.

---

## 🚀 Novidades da Versão `v1.7.0` (Fase 4)

* **📱 Acesso Remoto Mobile (LAN / Wi-Fi) sem Dependências:** Servidor HTTP nativo em socket TCP (`TcpListener`) escutando em `0.0.0.0:3333`, permitindo que qualquer smartphone ou tablet na mesma rede Wi-Fi acesse o painel instantaneamente sem necessidade de privilégios de administrador (`URLACL`).
* **📷 QR Code Integrado em Alta Resolução:** Botão `📱 Celular (QR)` na janela principal e no dashboard web que abre um modal com QR Code dinâmico apontando diretamente para o IP local da máquina (`http://<IP_LOCAL>:3333`). Basta apontar a câmera do celular para conectar.
* **🔊 Alarme Remoto com Web Audio API e Vibração:** O dashboard mobile permite armar um alerta sonoro com sintetizador de áudio web nativo e vibração contínua (`navigator.vibrate`) para despertar o usuário no celular mesmo longe do computador.
* **🔔 Notificações Nativas do Windows Tray (BalloonTip) com Foco em 1 Clique:** Alertas visuais na bandeja do sistema sempre que um agente solicitar intervenção humana. Clicar no balão restaura a janela e foca automaticamente no terminal do agente.
* **⚙️ Painel de Preferências e Sons Personalizados:** Subjanela de configurações permitindo selecionar entre Voz Sintetizada em Português ("Voz Maria"), Bipe clássico ou Silencioso, ajustar a frequência do alarme (2s, 4s, 8s ou 15s) e cadastrar executáveis adicionais para vigilância.
* **📦 Executável 100% Standalone (Zero DLLs Externas):** A biblioteca `QRCoder.dll` e a interface web `index.html` são embutidas diretamente como recursos dentro do único binário `AIProcessMonitor.exe`, carregadas em memória sob demanda.

---

## 🌟 Funcionalidades Completas

* **Detecção Inteligente de Ação Humana:** Identifica quando um agente CLI (como o Google Antigravity `agy`, Claude Code, Cursor, Windsurf, Aider, Ollama, LM Studio, etc.) entra em pausa aguardando autorização de comando, confirmação de escrita de arquivo ou respostas interativas.
* **Síntese de Voz Nativa em Português (pt-BR):** Alerta verbal contextualizado ("Atenção: Google Antigravity aguarda sua autorização").
* **Subjanela de Detalhes com Foco em 1 Clique:** Clique em qualquer processo para visualizar métricas completas (PID, Caminho, Linha de comando, Memória, CPU, Tempo ativo, Janela ativa) e gráfico de telemetria em tempo real (GDI+). Clicar na subjanela transfere o foco diretamente para a janela do processo.
* **Modo Não Perturbe (DND):** Pausa rápida de alarmes por 15m, 30m, 1h ou tempo indeterminado.
* **Busca e Filtros Instantâneos:** Localização em tempo real por nome, PID, categoria ou motivo de status.
* **Exportação Completa de Dados:** Download de relatórios em JSON e CSV direto pela interface ou via API.
* **Histórico de Auditoria Persistente:** Gravação local (`alert_history.json`) de incidentes de espera por ação humana com cálculo de tempo médio de resposta.
* **Webhooks Discord / Slack / Teams / Custom:** Disparo automático de payloads JSON para qualquer endpoint HTTP/HTTPS ao detectar alertas.

---

## 📦 Como Usar o Executável

1. **Modo Janela Nativa (Padrão):**
   * Dê dois cliques em **`AIProcessMonitor.exe`**.
   * A janela desktop abrirá exibindo o painel completo.

2. **Acesso pelo Celular:**
   * Clique no botão **`📱 Acesso Mobile`** no topo da janela ou acesse o menu da bandeja do sistema.
   * Aponte a câmera do seu celular para o QR Code exibido.
   * No celular, toque no botão **`🔊 Alarme Celular`** para ativar alertas sonoros e vibração remota.

3. **Modo Headless / Servidor em Segundo Plano:**
   ```powershell
   .\AIProcessMonitor.exe --headless
   ```

*(Opcional: porta personalizada como argumento: `AIProcessMonitor.exe 4000`)*

---

## 📡 Endpoints da API REST Nativa

* `GET /`: Dashboard Web integrado responsivo.
* `GET /api/processes`: Lista de processos monitorados com telemetria e status.
* `GET /api/network`: IP da LAN, porta e URLs de acesso.
* `GET /api/qr`: Imagem PNG com o QR Code dinâmico para acesso mobile.
* `GET /api/focus?pid={pid}`: Traz a janela do processo para frente da tela.
* `GET /api/kill?pid={pid}`: Encerra o processo especificado.
* `GET /api/mute?duration={15m|30m|1h|indefinite|unmute}`: Controle do Não Perturbe.
* `GET /api/export?format={json|csv}`: Exportação de dados.
* `GET /api/health`: Healthcheck da aplicação.
* `GET /api/history`: Histórico de auditoria e métricas de tempo de resposta.
* `GET /api/config` & `POST /api/config`: Leitura e gravação de preferências e webhooks.
* `GET /api/webhook/test`: Envio de notificação de teste.

---

## 🛠️ Compilação

Para compilar o binário autônomo:
* Execute `build.bat` ou utilize o comando:
  ```powershell
  & "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /target:winexe /out:AIProcessMonitor.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.Web.Extensions.dll /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\System.Speech.dll" /r:lib\QRCoder.dll /resource:lib\QRCoder.dll,QRCoder.dll /resource:public\index.html,index.html Program.cs
  ```

---

## 📁 Estrutura do Projeto

```
C:\Users\Julian\Dev\AIProcessMonitor\
├── AIProcessMonitor.exe   # Executável nativo standalone v1.7.0
├── Program.cs             # Código-fonte principal C# (.NET Framework 4.0)
├── build.bat              # Script de compilação em 1 clique
├── lib/
│   └── QRCoder.dll        # Biblioteca de geração de QR Code (.NET 4.0 embutida no binário)
├── public/
│   └── index.html         # Frontend web integrado responsivo com Web Audio & Vibração
├── appsettings.json       # Configurações de preferências, webhooks e sons (gerado automaticamente)
├── alert_history.json     # Histórico persistente de auditoria (gerado automaticamente)
└── README.md
```
