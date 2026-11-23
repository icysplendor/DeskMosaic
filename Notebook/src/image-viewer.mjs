// Reserve the new tab during the double-click gesture, before awaiting local
// storage/network. No note editor, credentials or scripts are copied into it.
export async function viewBrowserImage(path, readBinary) {
  const viewer = window.open("about:blank", "_blank");
  if (!viewer) throw Error("请允许打开新窗口以查看图片");
  viewer.opener = null;
  try {
    const data = await readBinary(path);
    if (data === null) throw Error("附件尚未下载，请联网后重试");
    if (viewer.closed) return;
    const extension = path.split(".").at(-1).toLowerCase();
    const type = {
      png: "png",
      jpg: "jpeg",
      jpeg: "jpeg",
      gif: "gif",
      webp: "webp",
      avif: "avif",
      bmp: "bmp",
      svg: "svg+xml",
      ico: "x-icon",
    }[extension];
    if (!type) throw Error("不支持的图片格式");
    const doc = viewer.document;
    doc.title = path.split("/").at(-1);
    const viewport = doc.createElement("meta");
    viewport.name = "viewport";
    viewport.content = "width=device-width, initial-scale=1";
    doc.head.append(viewport);
    doc.body.style.cssText =
      "margin:0;background:#181b22;display:grid;place-items:center;min-height:100dvh";
    const image = doc.createElement("img");
    image.alt = doc.title;
    image.style.cssText = "max-width:100%;max-height:100dvh;object-fit:contain";
    image.src = `data:image/${type};base64,${data}`;
    doc.body.replaceChildren(image);
  } catch (error) {
    viewer.close();
    throw error;
  }
}
