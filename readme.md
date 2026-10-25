# Scarpa Connection Manager

A robust, native desktop connection manager built for system administrators and engineers. Scarpa provides a unified interface for managing and automating SSH, SFTP, and RDP sessions with advanced networking, secure credential storage, and deep session automation.

## Core Features

### Security & Data Protection
* **Local Data Encryption:** All server configurations, including passwords and connection details, are AES-encrypted locally on the machine (`ssh_servers.json.enc`). 
* **Key-Based Authentication:** Full support for RSA/Ed25519 private key files, alongside standard password and keyboard-interactive authentication.

### Advanced Terminal & Appearance
* **Native Rendering:** High-performance hardware-accelerated terminal powered by WebView2 and xterm.js.
* **Complete Theming:** Highly customizable appearance supporting standard color palettes (Tango, XTerm, Rxvt) alongside custom hex foreground, background, and cursor definitions.
* **Font Control:** Adjustable font families and sizing (e.g., `Consolas 11`) to ensure perfect readability.
* **Scroll Buffer:** Configurable scrollback history to retain thousands of lines of output for review.

### Session Automation & Scripting
* **Login Actions (Expect/Send):** Automate complex login sequences, custom prompts, or secondary authentication requests by configuring the terminal to wait for specific string matches and automatically inject responses.
* **Startup Commands:** Automatically execute a predefined script or command file on the remote host immediately after a successful login sequence completes.
* **Password Injection:** Seamlessly bridges the gap for CLI tools by utilizing secure, self-destructing VBScript automation to inject credentials into native Windows SFTP prompts.

### Connection Resilience & Auditing
* **Anti-Idle (Keep-Alive):** Prevents session drops from strict firewalls or load balancers by transmitting configurable payload strings at designated intervals.
* **Comprehensive Logging:** Route terminal output directly to local files with optional millisecond timestamping.
* **Log Formatting:** Append to existing logs or overwrite them. Supports injecting highly customized, variable-driven (`%H`, `%Y`, `%m`) strings into the log file upon connection establishment and disconnection.

### Advanced Networking & Routing
* **Port Forwarding:** Define granular SSH tunneling rules per-server, supporting:
  * **Local Forwarding:** Route local ports to internal network resources.
  * **Remote Forwarding:** Expose local services to the remote host.
  * **Dynamic Forwarding:** Establish SOCKS5 proxies for secure web browsing.
* **Jump Servers:** Seamlessly route SSH and SFTP connections through an intermediate Bastion host to access firewalled internal infrastructure.

### Windows Remote Desktop (RDP)
* **Native Integration:** Generates on-the-fly `.rdp` configurations to leverage the native Windows `mstsc.exe` client.
* **Session Tuning:** Full control over audio redirection, clipboard sharing, certificate ignoring, and local drive mapping.
* **Resolution Management:** Launch in full-screen mode or explicitly define custom window resolutions (e.g., `1920x1080`).