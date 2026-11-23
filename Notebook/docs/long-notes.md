# 长笔记渲染

参考的成熟实现：

- [CodeMirror EditorView](https://github.com/codemirror/view/blob/main/src/editorview.ts) 与 [viewport 指南](https://codemirror.net/docs/guide/#viewport)：正文状态与绘制范围分离；可见区域外保留缓冲区，并将布局测量和 DOM 写入分开。
- [VS Code TokenizerSyntaxTokenBackend](https://github.com/microsoft/vscode/blob/main/src/vs/editor/common/model/tokens/tokenizerSyntaxTokenBackend.ts)：可见范围变化时优先更新对应高亮。
- [VS Code DefaultBackgroundTokenizer](https://github.com/microsoft/vscode/blob/main/src/vs/editor/common/model/textModelTokens.ts)：后台高亮分批执行，并让出执行时间，避免一次长任务阻塞交互。
- [Vditor codeRender](https://github.com/Vanessa219/vditor/blob/master/src/ts/markdown/codeRender.ts)：原复制工具读取 `innerText` 并逐块写样式；可编辑预览加载大量代码块时，这会反复触发布局。本项目通过其 `customRenders` 扩展接口替换普通代码块处理。

本次借鉴设计，没有复制上述项目的实现代码，也没有替换编辑器。Vditor 的可编辑 DOM 是其全文序列化输入，直接移除屏幕外正文可能丢失保存内容；完整虚拟编辑需要独立文档模型，不能只删除 DOM 节点。

目前完整正文仍一次载入；延迟的是语言识别与语法高亮。距离可见区域 300px 内的代码块进入任务队列，一次处理一块。复制和语言选择工具立即可用，复制始终读取原始源码。文字、表格及代码正文不等待高亮，也不加载额外编辑器实例。普通正文的换行适配先检查是否需要转换，避免无关代码块导致再次解析全文 DOM。

切换笔记、替换正文时清空待处理任务；删除或重新渲染块时释放对应观察对象。队列仅持有当前文档的待处理节点，语言检测缓存上限仍为 32 条。关闭预览切换到源码后，原有未完成任务不会修改源码。

浏览器验收：启动 `Notebook/tests/preview-server.mjs`（4173）和隔离 Chrome CDP（9223），运行 `python3 Notebook/tests/long-note-browser-checks.py`。测试使用 `/tmp/notebook-ui-test-library`，会重建该测试库，不接触实际笔记。覆盖千段正文、80 个代码块、未高亮块完整复制、底部编辑保存、模式切换和旧任务取消。耗时为当前测试机器的测量值，不是所有设备的保证；极长单块或纯正文仍受全文解析和 DOM 大小影响。

本机同配置对照：80 个代码块从约 3120ms 降为 247ms；千段普通正文为旧版约 400ms、新版约 442ms，没有显著改善。对照需使用 `Network.setBypassServiceWorker`，避免 PWA 将基准页面替换为当前缓存版本。本次不宣称实现全文虚拟化，也不宣称降低纯正文的解析复杂度。

回归中同时发现旧版模式切换的空行累积：Lute 在表格前输出额外格式空行，空段落适配将其误当成用户间隔。仅在 DOM 序列化结果中去掉该格式空行；真实空段落已编码为 `<br />`，导入 Markdown 的原始空行与代码块内空行仍保留。混合标题、表格、列表与长代码的首行定位回归用于防止再次漂移。
