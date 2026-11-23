import { decode } from "./core.mjs";

// Standard Depth:1 PROPFIND works with Nextcloud and generic WebDAV servers.
// Carry ETags from successful properties instead of downloading every file.
export async function listRemoteFiles(request, url, progress = () => {}) {
  const paths = [],
    errors = [],
    folders = [""],
    seen = new Set();
  const baseUrl = new URL(url),
    base = baseUrl.pathname;
  let visited = 0;
  while (folders.length) {
    const batch = folders.splice(0, 3).filter((path) => {
      if (seen.has(path)) return false;
      seen.add(path);
      return true;
    });
    if ((visited += batch.length) > 10000)
      throw new Error("在线目录过大，请选择专用笔记文件夹");
    progress(visited);
    await Promise.all(
      batch.map(async (path) => {
        try {
          const found = [],
            nextFolders = [];
          const response = await request("PROPFIND", path, null, {
            Depth: "1",
          });
          if (response.status !== 207)
            throw new Error(`在线目录读取失败 (${response.status})`);
          const xml = new DOMParser().parseFromString(
            decode(response.data),
            "application/xml",
          );
          if (
            xml.documentElement.localName !== "multistatus" ||
            xml.documentElement.namespaceURI !== "DAV:" ||
            xml.querySelector("parsererror")
          )
            throw new Error("服务器目录响应无效");
          let selfSeen = false;
          for (const item of xml.getElementsByTagNameNS("DAV:", "response")) {
            const href = item.getElementsByTagNameNS("DAV:", "href")[0]
              ?.textContent;
            if (!href) throw new Error("在线目录响应缺少文件路径，已停止同步");
            const itemUrl = new URL(href, url),
              urlPath = itemUrl.pathname;
            if (itemUrl.origin !== baseUrl.origin || !urlPath.startsWith(base))
              continue;
            const relative = decodeURIComponent(
              urlPath.slice(base.length),
            ).replace(/\/$/, "");
            const self = relative === path.replace(/\/$/, "");
            if (
              !self &&
              (!/^(books|\.deleted|\.deleted-books)(\/|$)/.test(relative) ||
                relative.split("/").some((x) => !x || x === ".." || x === "."))
            )
              continue;
            const valid = [
              ...item.getElementsByTagNameNS("DAV:", "propstat"),
            ].filter((stat) =>
              /\s200(?:\s|$)/.test(
                stat.getElementsByTagNameNS("DAV:", "status")[0]?.textContent ||
                  "",
              ),
            );
            if (
              !valid.some(
                (stat) =>
                  stat.getElementsByTagNameNS("DAV:", "resourcetype").length,
              )
            )
              throw new Error("在线文件属性读取失败，已停止同步");
            if (self) {
              selfSeen = true;
              if (
                !valid.some(
                  (stat) =>
                    stat.getElementsByTagNameNS("DAV:", "collection").length,
                )
              )
                throw new Error("在线笔记目录不是文件夹，已停止同步");
              continue;
            }
            if (
              valid.some(
                (stat) =>
                  stat.getElementsByTagNameNS("DAV:", "collection").length,
              )
            )
              nextFolders.push(relative + "/");
            else
              found.push({
                path: relative,
                etag:
                  valid
                    .flatMap((stat) => [
                      ...stat.getElementsByTagNameNS("DAV:", "getetag"),
                    ])
                    .at(0)
                    ?.textContent?.trim() || "",
              });
          }
          if (!selfSeen)
            throw new Error("在线目录响应不完整，已停止同步且未删除文件");
          paths.push(...found);
          folders.push(...nextFolders);
        } catch (error) {
          if (!path || error.status === 401 || error.code === "SYNC_LEASE_LOST")
            throw error;
          // Never interpret an unreadable subtree as deleted. Other complete
          // subtrees can still synchronize and report their own outcome.
          errors.push({
            path,
            message: error.message,
            code: error.code || "SYNC_SCAN_ERROR",
            status: error.status || 0,
            retryAfter: error.retryAfter || 0,
          });
        }
      }),
    );
  }
  paths.errors = errors;
  return paths;
}
