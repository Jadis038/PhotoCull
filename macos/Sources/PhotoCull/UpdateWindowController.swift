// 影·筛 / PhotoCull — 更新提示窗口
import AppKit

final class UpdateWindowController: NSWindowController, NSWindowDelegate {
    private let titleLabel = NSTextField(labelWithString: "")
    private let subtitleLabel = NSTextField(labelWithString: "")
    private let notesTextView = NSTextView()
    private let progressIndicator = NSProgressIndicator()
    private let statusLabel = NSTextField(labelWithString: "")
    private let installButton = NSButton(title: "下载并安装", target: nil, action: nil)
    private let laterButton = NSButton(title: "以后再说", target: nil, action: nil)

    private var release: UpdateRelease?
    private var isInstalling = false

    init() {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 580, height: 500),
                              styleMask: [.titled, .closable],
                              backing: .buffered, defer: false)
        window.title = "发现新版本"
        window.isReleasedWhenClosed = false
        window.center()
        super.init(window: window)
        window.delegate = self
        buildUI()
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    private func buildUI() {
        guard let content = window?.contentView else { return }

        titleLabel.font = NSFont.systemFont(ofSize: 20, weight: .semibold)
        subtitleLabel.font = NSFont.systemFont(ofSize: 12)
        subtitleLabel.textColor = NSColor.secondaryLabelColor

        let notesScroll = NSScrollView()
        notesScroll.translatesAutoresizingMaskIntoConstraints = false
        notesScroll.hasVerticalScroller = true
        notesScroll.autohidesScrollers = true
        notesScroll.drawsBackground = false
        notesScroll.borderType = .noBorder
        notesTextView.isEditable = false
        notesTextView.isSelectable = true
        notesTextView.drawsBackground = false
        notesTextView.font = NSFont.systemFont(ofSize: 12)
        notesTextView.textContainerInset = NSSize(width: 8, height: 6)
        notesTextView.isVerticallyResizable = true
        notesTextView.isHorizontallyResizable = false
        notesTextView.autoresizingMask = [.width]
        notesTextView.frame = NSRect(x: 0, y: 0, width: 520, height: 220)
        notesTextView.textContainer?.widthTracksTextView = true
        notesScroll.documentView = notesTextView

        progressIndicator.style = .bar
        progressIndicator.isIndeterminate = false
        progressIndicator.minValue = 0
        progressIndicator.maxValue = 1
        progressIndicator.isHidden = true

        statusLabel.font = NSFont.systemFont(ofSize: 11)
        statusLabel.textColor = NSColor.secondaryLabelColor

        installButton.bezelStyle = .rounded
        installButton.keyEquivalent = "\r"
        installButton.target = self
        installButton.action = #selector(installPressed)

        laterButton.bezelStyle = .rounded
        laterButton.target = self
        laterButton.action = #selector(laterPressed)

        let spacer = NSView()
        spacer.setContentHuggingPriority(.init(1), for: .horizontal)
        let buttonRow = NSStackView(views: [spacer, laterButton, installButton])
        buttonRow.orientation = .horizontal
        buttonRow.spacing = 10

        let stack = NSStackView(views: [titleLabel, subtitleLabel, notesScroll, progressIndicator, statusLabel, buttonRow])
        stack.translatesAutoresizingMaskIntoConstraints = false
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 10
        content.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -24),
            stack.topAnchor.constraint(equalTo: content.topAnchor, constant: 22),
            stack.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -20),
            notesScroll.widthAnchor.constraint(equalTo: stack.widthAnchor),
            notesScroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 220),
            progressIndicator.widthAnchor.constraint(equalTo: stack.widthAnchor),
            statusLabel.widthAnchor.constraint(equalTo: stack.widthAnchor),
            buttonRow.widthAnchor.constraint(equalTo: stack.widthAnchor)
        ])
    }

    func present(release: UpdateRelease) {
        self.release = release
        isInstalling = false
        titleLabel.stringValue = "影·筛 \(release.version) 可用"
        subtitleLabel.stringValue = "当前版本 \(AppInfo.version) · 发布于 GitHub Releases"
        notesTextView.string = release.notes
        progressIndicator.isHidden = true
        progressIndicator.doubleValue = 0
        statusLabel.stringValue = ""
        installButton.isEnabled = true
        laterButton.isEnabled = true
        installButton.title = "下载并安装"
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc private func installPressed() {
        guard let release, !isInstalling else { return }
        isInstalling = true
        installButton.isEnabled = false
        laterButton.isEnabled = false
        progressIndicator.isHidden = false
        progressIndicator.doubleValue = 0
        statusLabel.stringValue = "正在下载 \(release.assetName)…"

        UpdateChecker.shared.download(release, progress: { [weak self] value in
            self?.progressIndicator.doubleValue = value
            self?.statusLabel.stringValue = "正在下载 \(Int(value * 100))%…"
        }, completion: { [weak self] result in
            guard let self else { return }
            switch result {
            case .success(let dmgURL):
                self.statusLabel.stringValue = "正在安装，应用即将重启…"
                UpdateChecker.shared.installDownloadedUpdate(dmgURL: dmgURL) { installResult in
                    switch installResult {
                    case .success:
                        self.statusLabel.stringValue = "安装完成，正在重启…"
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) {
                            NSApp.terminate(nil)
                        }
                    case .failure(let error):
                        self.finishWithError(error)
                    }
                }
            case .failure(let error):
                self.finishWithError(error)
            }
        })
    }

    private func finishWithError(_ error: Error) {
        isInstalling = false
        installButton.isEnabled = true
        laterButton.isEnabled = true
        progressIndicator.isHidden = true
        statusLabel.stringValue = ""
        let alert = NSAlert()
        alert.messageText = "更新失败"
        alert.informativeText = error.localizedDescription
        alert.addButton(withTitle: "好")
        if let window { alert.beginSheetModal(for: window, completionHandler: nil) } else { alert.runModal() }
    }

    @objc private func laterPressed() {
        window?.close()
    }

    func windowWillClose(_ notification: Notification) {
        // 只关闭更新窗口，主窗口仍在
    }
}
