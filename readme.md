# Scarpa Connection Manager

A robust, natively compiled WinUI 3 desktop connection manager engineered for system administrators, DevOps professionals, and network engineers. Scarpa provides a unified, hardware-accelerated interface for managing, securing, and heavily automating SSH, SFTP, and RDP sessions. 

Built on .NET 8 as a completely self-contained deployment, Scarpa requires no external runtimes and integrates seamlessly into the Windows 11 ecosystem.

---

## 🛡️ Security & Data Protection

Security is handled entirely locally; Scarpa never transmits configuration data to external servers.

*   **AES-Encrypted Local Storage:** All server configurations, credentials, port forwarding rules, and automation scripts are heavily encrypted at rest. Data is stored locally in `%LocalAppData%\ScarpaConnectionManager\ssh_servers.json.enc` utilizing robust AES encryption, ensuring that even if the physical drive is compromised, your infrastructure credentials remain secure.
*   **Comprehensive Authentication Support:** 
    *   Standard Password & Keyboard-Interactive authentication.
    *   Native support for RSA, DSA, ECDSA, and Ed25519 private key files (`-i` identity files).
    *   Password bypass for SSH agent workflows.

---

## 🖥️ Advanced Terminal & Rendering

Scarpa abandons the legacy Windows Console in favor of a modern, web-standard rendering pipeline.

*   **Hardware-Accelerated UI:** The terminal interface is powered by a locally hosted Microsoft WebView2 control running `xterm.js`. This guarantees high-speed rendering, flawless ANSI escape sequence parsing, and true-color support.
*   **Deep Theming & Customization:**
    *   Select from industry-standard color palettes including **Tango**, **XTerm**, and **Rxvt**.
    *   Override default palettes with explicit hexadecimal color codes for Foreground, Background, and Cursor elements.
    *   Dynamic background color injection ensures the entire WebView2 canvas perfectly matches the terminal background.
*   **Typography Control:** Define custom font families and sizes (e.g., `Consolas 11`, `Fira Code 10`) for optimal readability on high-DPI displays.
*   **Configurable Scroll Buffer:** Define exact line limits for the scrollback buffer to balance memory usage with the need to review thousands of lines of historical console output.

---

## ⚙️ Session Automation & Scripting

Scarpa is designed to eliminate repetitive administrative tasks through deep connection automation.

*   **Login Actions (Expect/Send Engine):** A powerful, asynchronous stream-reading engine that monitors incoming terminal text for specific string matches (e.g., `Verification code:`, `Enter secondary password:`). Once a match is detected in the rolling buffer, Scarpa instantly injects a predefined response. This is invaluable for automating multi-factor authentication (MFA) flows or navigating custom bastion host menus.
*   **Post-Login Startup Commands:** Automatically execute a sequence of commands or a complete script file directly on the remote host the millisecond the authentication sequence and Login Actions conclude.
*   **Secure VBScript Password Injection:** Windows native SFTP clients do not natively support passing passwords via standard input. Scarpa circumvents this by dynamically generating a secure, self-destructing VBScript. This script polls the OS for the specific SFTP window handle, injects the obfuscated keystrokes, triggers the authentication, and instantly deletes itself from the filesystem.

---

## 📡 Advanced Networking & Routing

Complex infrastructure requires complex routing. Scarpa handles complex SSH tunneling seamlessly.

*   **Jump Servers (Bastion Hosts):** Seamlessly proxy SSH and SFTP sessions through an intermediate Bastion server. Scarpa securely passes the internal routing commands behind the scenes, allowing you to connect to completely isolated, firewalled internal nodes with a single click.
*   **Granular Port Forwarding:** Define permanent SSH tunnels on a per-server basis.
    *   **Local Forwarding (`-L`):** Bind a local port (e.g., `127.0.0.1:8080`) to a remote internal resource (e.g., `10.0.0.5:80`), routing traffic securely through the SSH tunnel.
    *   **Remote Forwarding (`-R`):** Expose a service running on your local machine to the remote host.
    *   **Dynamic Forwarding (`-D`):** Transform the SSH connection into a local SOCKS5 proxy for secure, encrypted web browsing or application routing.

---

## 📝 Auditing & Connection Resilience

*   **Anti-Idle (Keep-Alive):** Keep long-running sessions alive behind strict corporate firewalls or aggressive NAT routers. Configure custom payload strings (e.g., a null byte or whitespace) and exact transmission intervals (in seconds) to prevent premature session termination.
*   **Intelligent Logging Engine:** 
    *   Route raw terminal stdout/stderr directly to local `.log` files.
    *   Optionally strip ANSI color codes from logs for clean plaintext reading.
    *   Toggle between **Append** (maintaining history across sessions) or **Overwrite** modes.
    *   Inject custom, variable-driven strings into the log file exactly when a session connects or disconnects (Supports variables like `%H` for Host, `%Y` for Year, `%m` for minute, etc.).
    *   Optionally prepend exact millisecond timestamps to every single line of terminal output for strict auditing.

---

## 🪟 Windows Remote Desktop (RDP) Integration

*   **Dynamic Configuration:** Scarpa does not reinvent the RDP wheel; it generates highly optimized, temporary `.rdp` files on-the-fly and hands them off to the native Windows `mstsc.exe` client for maximum stability and performance.
*   **Session Tuning:** 
    *   Toggle Audio Redirection (Play on remote vs Play locally).
    *   Enable/Disable Clipboard sharing.
    *   Toggle local Drive Mapping (`drivestoredirect`).
    *   Bypass or enforce strict SSL Certificate validation.
*   **Resolution Management:** Launch sessions in seamless Full-Screen mode, or lock them to explicit custom resolutions (e.g., `1920x1080`, `1280x720`) to fit within specific monitor zones.