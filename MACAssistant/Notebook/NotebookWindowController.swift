import AppKit
import WebKit

@MainActor
final class NotebookWindowController: NSObject, WKScriptMessageHandler, WKNavigationDelegate, NSWindowDelegate {
    static let shared = NotebookWindowController()
    private var window: NSWindow?
    private var browser: WKWebView?
    private var pendingIntent: String?
    private var ready = false
    private var maintenanceTimer: Timer?
    private var syncTimer: Timer?
    private var codeEditor: NotebookCodeWindow?
    private lazy var service = NotebookService(theme: SettingsManager.shared.theme)

    func start() {
        guard window == nil else { return }
        let configuration = WKWebViewConfiguration()
        configuration.userContentController.add(self, name: "notebook")
        configuration.preferences.javaScriptCanOpenWindowsAutomatically = false
        let web = NotebookWebView(frame: NSRect(x: 0, y: 0, width: 1280, height: 860), configuration: configuration)
        web.navigationDelegate = self
        // AppKit owns native resize edges outside WKWebView. Windows reserves
        // an inner 10-DIP hit region because WebView2's child HWND intercepts it.
        let window = NSWindow(contentRect: web.frame, styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView], backing: .buffered, defer: false)
        window.title = "笔记本"; window.titleVisibility = .hidden; window.titlebarAppearsTransparent = true
        window.isReleasedWhenClosed = false; window.minSize = NSSize(width: 900, height: 600)
        let content = NSView(frame: web.frame)
        let header = NotebookTitlebar(frame: NSRect(x: 0, y: 822, width: 1280, height: 38))
        header.autoresizingMask = [.width, .minYMargin]
        web.frame = NSRect(x: 0, y: 0, width: 1280, height: 822)
        web.autoresizingMask = [.width, .height]
        content.addSubview(web); content.addSubview(header)
        let icon = NSImageView(frame: NSRect(x: 84, y: 7, width: 24, height: 24))
        icon.image = NSImage(named: "AppEmblem"); header.addSubview(icon)
        let title = NSTextField(labelWithString: "笔记本")
        title.frame = NSRect(x: 117, y: 8, width: 200, height: 22)
        title.font = .systemFont(ofSize: 14, weight: .semibold); header.addSubview(title)
        let theme = NSButton(title: "◐", target: self, action: #selector(toggleTheme))
        theme.frame = NSRect(x: 1228, y: 3, width: 36, height: 32)
        theme.autoresizingMask = [.minXMargin]; theme.bezelStyle = .texturedRounded
        theme.toolTip = "切换浅色或深色"; header.addSubview(theme)
        window.contentView = content; window.delegate = self; window.center()
        window.isMovableByWindowBackground = false
        self.window = window; browser = web
        guard let resources = Bundle.main.url(forResource: "NotebookWeb", withExtension: nil), FileManager.default.fileExists(atPath: resources.appendingPathComponent("index.html").path) else {
            let label = NSTextField(labelWithString: "离线笔记资源缺失，请重新安装软件")
            label.frame = NSRect(x: 30, y: 30, width: 500, height: 40); window.contentView?.addSubview(label); return
        }
        web.loadFileURL(resources.appendingPathComponent("index.html"), allowingReadAccessTo: resources)
        syncTimer = Timer.scheduledTimer(withTimeInterval: 300, repeats: true) { _ in
            Task { @MainActor in NotebookWindowController.shared.synchronize() }
        }
        maintenanceTimer = Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { _ in
            // Resolve the singleton on the main actor; do not pass a mutable
            // weak capture from the timer callback into a concurrent task.
            Task { @MainActor in
                NotebookWindowController.shared.wake()
            }
        }
    }
    @objc private func toggleTheme() {
        guard ready, codeEditor == nil else { return }
        browser?.evaluateJavaScript("document.getElementById('theme').click()", completionHandler: nil)
    }
    private func applyTheme(_ settings: Any) {
        let dark = (settings as? [String: Any])?["theme"] as? String == "Dark"
        window?.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        window?.backgroundColor = dark ? NSColor(calibratedRed: 0.067, green: 0.106, blue: 0.169, alpha: 1) : NSColor(calibratedRed: 0.98, green: 0.988, blue: 1, alpha: 1)
    }
    private func navigate() {
        guard ready, let intent = pendingIntent else { return }
        pendingIntent = nil
        // Intent comes only from the two native commands.
        browser?.evaluateJavaScript("window.notebookNavigate?.('\(intent == "quick" ? "quick" : "last")')", completionHandler: nil)
    }
    func show(intent: String = "last") {
        if let codeEditor { NSApp.activate(ignoringOtherApps: true); codeEditor.show(); return }
        pendingIntent = intent
        start(); guard let window else { return }
        if window.isMiniaturized { window.deminiaturize(nil) }
        NSApp.activate(ignoringOtherApps: true); window.makeKeyAndOrderFront(nil); navigate()
    }
    func synchronize() { guard ready, codeEditor == nil else { return }; browser?.evaluateJavaScript("window.notebookSync?.()", completionHandler: nil) }
    func wake() { guard ready, codeEditor == nil else { return }; browser?.evaluateJavaScript("window.notebookWake?.()", completionHandler: nil) }
    func flushBeforeQuit() async throws {
        if codeEditor != nil {
            throw NSError(domain: "Notebook", code: 1, userInfo: [NSLocalizedDescriptionKey: "请先保存或关闭代码编辑窗口"])
        }
        guard ready, let browser else { return }
        let script = """
        await Promise.race([
          window.notebookFlush(),
          new Promise((_, reject) => setTimeout(() => reject(new Error('笔记保存超过 30 秒，请等待当前操作完成后再退出')), 30000))
        ]);
        """
        _ = try await browser.callAsyncJavaScript(script, arguments: [:], in: nil, contentWorld: .page)
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if let codeEditor { codeEditor.show(); return false }
        guard ready else { sender.orderOut(nil); return false }
        browser?.evaluateJavaScript("window.notebookClose?.()", completionHandler: nil); return false
    }
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.frameInfo.isMainFrame,
              message.frameInfo.request.url?.lastPathComponent == "index.html",
              message.frameInfo.request.url?.deletingLastPathComponent().lastPathComponent == "NotebookWeb",
              let body = message.body as? [String: Any], let id = body["id"] as? Int, let op = body["op"] as? String, let args = body["args"] as? [String: Any] else { return }
        Task { @MainActor in
            do {
                var result: Any = NSNull()
                switch op {
                case "settings": result = await service.settings(); applyTheme(result)
                case "theme": try await service.setTheme(args["value"] as? String ?? "Light"); result = await service.settings(); applyTheme(result)
                case "configure": result = try await service.configure(args)
                case "readClipboard": result = NSPasteboard.general.string(forType: .string) ?? ""
                case "clipboard": NSPasteboard.general.clearContents(); NSPasteboard.general.setString(args["text"] as? String ?? "", forType: .string)
                case "editCode":
                    guard codeEditor == nil else { throw NSError(domain: "Notebook", code: 2, userInfo: [NSLocalizedDescriptionKey: "代码编辑窗口已打开"]) }
                    let edited: [String: Any]? = await withCheckedContinuation { continuation in
                        let editor = NotebookCodeWindow(args: args) { [weak self] text in
                            self?.codeEditor = nil
                            continuation.resume(returning: text)
                        }
                        codeEditor = editor
                        editor.show()
                    }
                    result = edited as Any? ?? NSNull()
                // Images use the system viewer, never an editor-blocking overlay.
                case "open", "viewImage": let url = try await service.resolve(args["path"] as? String ?? ""); NSWorkspace.shared.open(url)
                case "saveAttachment":
                    let source = try await service.resolve(args["path"] as? String ?? "")
                    let panel = NSSavePanel()
                    panel.nameFieldStringValue = (args["name"] as? String ?? source.lastPathComponent)
                    panel.canCreateDirectories = true
                    if panel.runModal() == .OK, let destination = panel.url {
                        try await service.exportAttachment(args["path"] as? String ?? "", to: destination)
                        result = true
                    } else { result = false }
                case "external":
                    if let string = args["url"] as? String, let url = URL(string: string), ["https", "http"].contains(url.scheme ?? "") { NSWorkspace.shared.open(url) }
                case "window":
                    switch args["action"] as? String {
                    case "close": window?.orderOut(nil)
                    case "minimize": window?.miniaturize(nil)
                    case "maximize": window?.zoom(nil)
                    case "drag": if let event = NSApp.currentEvent { window?.performDrag(with: event) }
                    default: break
                    }
                case "ready": ready = true; navigate()
                default: result = try await service.execute(op, args: args)
                }
                reply(id, result: result, error: nil)
            } catch { reply(id, result: NSNull(), error: error.localizedDescription) }
        }
    }
    private func reply(_ id: Int, result: Any, error: String?) {
        guard let data = try? JSONSerialization.data(withJSONObject: [result, error as Any? ?? NSNull()], options: [.fragmentsAllowed]), let json = String(data: data, encoding: .utf8) else { return }
        browser?.evaluateJavaScript("window.nativeReply(\(id), ...\(json))", completionHandler: nil)
    }
    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        if let url = navigationAction.request.url, let resources = Bundle.main.url(forResource: "NotebookWeb", withExtension: nil), url.isFileURL, url.standardizedFileURL.path == resources.appendingPathComponent("index.html").standardizedFileURL.path { decisionHandler(.allow) }
        else { decisionHandler(.cancel) }
    }
}

@MainActor
private final class NotebookWebView: WKWebView {
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection([.command, .shift, .control, .option])
        guard modifiers.subtracting(.shift) == .command else { return super.performKeyEquivalent(with: event) }
        // Accessory apps have no main Edit menu. Dispatch standard editing
        // actions to WebKit's first responder, so Cmd+V produces a real paste
        // event with text/plain and Cmd+X/C can use the shared code handlers.
        let action: String
        switch event.charactersIgnoringModifiers?.lowercased() {
        case "c": action = "copy:"
        case "x": action = "cut:"
        case "v": action = "paste:"
        case "s" where url?.lastPathComponent == "code-editor.html":
            evaluateJavaScript("window.codeEditorSave?.()", completionHandler: nil)
            return true
        case "a", "z":
            let key = event.charactersIgnoringModifiers?.lowercased() ?? "a"
            let shift = modifiers.contains(.shift) ? "true" : "false"
            // Let the shared code selection and Markdown undo history run
            // before falling back to a focused ordinary text field.
            evaluateJavaScript("""
            (() => {
              const target = document.activeElement;
              if (target && target.dispatchEvent(new KeyboardEvent('keydown', {key:'\(key)',metaKey:true,shiftKey:\(shift),bubbles:true,cancelable:true})))
                document.execCommand('\(key == "a" ? "selectAll" : (modifiers.contains(.shift) ? "redo" : "undo"))');
            })()
            """, completionHandler: nil)
            return true
        default: return super.performKeyEquivalent(with: event)
        }
        return NSApp.sendAction(Selector(action), to: nil, from: self) || super.performKeyEquivalent(with: event)
    }
}

// A standalone native window hosts the shared offline editor. Do not attach
// it with addChildWindow: AppKit then moves it whenever the notebook moves.
@MainActor
private final class NotebookCodeWindow: NSObject, NSWindowDelegate, WKScriptMessageHandler, WKNavigationDelegate {
    let window: NSWindow
    private var browser: NotebookWebView!
    private let initial: [String: Any]
    private var completion: (([String: Any]?) -> Void)?
    private var result: [String: Any]?
    private var allowClose = false
    private var ready = false
    private var editorURL: URL?

    init(args: [String: Any], completion: @escaping ([String: Any]?) -> Void) {
        initial = args; self.completion = completion
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 760), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        super.init()
        window.title = "代码块编辑"
        window.minSize = NSSize(width: 640, height: 420)
        window.isReleasedWhenClosed = false; window.delegate = self
        let dark = args["dark"] as? Bool == true
        window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        window.backgroundColor = dark ? NSColor(calibratedRed: 0.067, green: 0.106, blue: 0.169, alpha: 1) : NSColor(calibratedRed: 0.973, green: 0.984, blue: 1, alpha: 1)
        let configuration = WKWebViewConfiguration()
        configuration.userContentController.add(self, name: "codeEditor")
        configuration.preferences.javaScriptCanOpenWindowsAutomatically = false
        browser = NotebookWebView(frame: NSRect(x: 0, y: 0, width: 1100, height: 760), configuration: configuration)
        browser.navigationDelegate = self
        window.contentView = browser; window.center()
        if let resources = Bundle.main.url(forResource: "NotebookWeb", withExtension: nil) {
            let url = resources.appendingPathComponent("code-editor.html")
            editorURL = url.standardizedFileURL
            browser.loadFileURL(url, allowingReadAccessTo: resources)
        }
    }
    func show() {
        if window.isMiniaturized { window.deminiaturize(nil) }
        window.makeKeyAndOrderFront(nil); window.makeFirstResponder(browser)
    }
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.frameInfo.isMainFrame, let url = message.frameInfo.request.url,
              url.standardizedFileURL == editorURL,
              let body = message.body as? [String: Any], let op = body["op"] as? String,
              let args = body["args"] as? [String: Any] else { return }
        switch op {
        case "ready":
            guard let data = try? JSONSerialization.data(withJSONObject: initial), let json = String(data: data, encoding: .utf8) else { return }
            browser.evaluateJavaScript("window.codeEditorInitialize(\(json))", completionHandler: nil)
            ready = true
        case "save":
            guard let text = args["text"] as? String, let language = args["language"] as? String else { return }
            result = ["text": text, "language": language]; allowClose = true; window.close()
        case "discard": allowClose = true; window.close()
        case "clipboard":
            guard let id = body["id"] as? Int else { return }
            NSPasteboard.general.clearContents()
            let success = NSPasteboard.general.setString(args["text"] as? String ?? "", forType: .string)
            let error: Any = success ? NSNull() : "无法写入剪贴板"
            if let data = try? JSONSerialization.data(withJSONObject: [NSNull(), error]), let json = String(data: data, encoding: .utf8) {
                browser.evaluateJavaScript("window.nativeReply(\(id), ...\(json))", completionHandler: nil)
            }
        default: break
        }
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if allowClose || !ready { return true }
        browser.evaluateJavaScript("window.codeEditorRequestClose()", completionHandler: nil)
        return false
    }
    func windowWillClose(_ notification: Notification) {
        browser.configuration.userContentController.removeScriptMessageHandler(forName: "codeEditor")
        let callback = completion; completion = nil; callback?(result)
    }
    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        // Allow the initial document only; reload must not discard a draft.
        decisionHandler(!ready && navigationAction.request.url?.standardizedFileURL == editorURL ? .allow : .cancel)
    }
}

// Dragging stays in AppKit: an asynchronous WebKit message cannot retain the mouse event.
@MainActor
private final class NotebookTitlebar: NSView {
    override var mouseDownCanMoveWindow: Bool { true }
    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 { window?.zoom(nil) }
        else { window?.performDrag(with: event) }
    }
    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        NSColor.separatorColor.setFill()
        NSBezierPath(rect: NSRect(x: 0, y: 0, width: bounds.width, height: 1)).fill()
    }
}
