import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import {
  createNotesServer,
  validateLibrary,
  validateRequest,
} from "../server/server.mjs";
import { Accounts } from "../server/accounts.mjs";
const library = "https://cloud.test/remote.php/dav/files/vanong/Notes/";
test("relay rejects path escapes, other origins and unconditional mutations", () => {
  for (const url of [
    "http://127.0.0.1/remote.php/dav/files/vanong/Notes/",
    "https://cloud.test/remote.php/dav/files/other/Notes/",
    "https://cloud.test/remote.php/dav/files/vanong/",
    "https://cloud.test/remote.php/dav/files/vanong/%2Fescape/",
  ])
    assert.throws(() => validateLibrary(url, "vanong", "https://cloud.test"));
  for (const value of [
    { method: "PUT", path: "books/x/main.md", data: "" },
    { method: "DELETE", path: "books/" },
    { method: "GET", path: "books/../secret" },
    { method: "GET", path: "books/\\secret" },
    { method: "PROPFIND", path: "", headers: { Depth: "infinity" } },
    { method: "GET", path: "books/x", headers: { Authorization: "other" } },
    { method: "DELETE", path: ".deleted/", headers: { "If-Match": '"1"' } },
  ])
    assert.throws(() => validateRequest(value));
});
test("service authentication is separate; DAV credentials encrypted, requests versioned and account scoped", async () => {
  const dir = await mkdtemp(tmpdir() + "/notes-auth-"),
    configFile = dir + "/settings.enc";
  const accounts = new Accounts(configFile + ".sqlite");
  const token = accounts.invite();
  accounts.close();
  const calls = [];
  const fetcher = async (url, options) => {
    calls.push({ url, options });
    assert.equal(
      options.headers["Accept-Encoding"],
      "identity",
      "WebDAV ETags must not gain Apache's gzip representation suffix",
    );
    return new Response("", {
      status: options.method === "PROPFIND" ? 207 : 200,
      headers: { etag: '"1"', "Retry-After": "9" },
    });
  };
  const server = createNotesServer({
    origin: "http://notes.test",
    davOrigin: "https://cloud.test",
    secret: "test-secret-with-at-least-32-characters",
    configFile,
    secure: false,
    fetcher,
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const base = "http://127.0.0.1:" + server.address().port;
  let cookie;
  const post = (path, data, origin = "http://notes.test") =>
    fetch(base + "/api/" + path, {
      method: "POST",
      headers: {
        Origin: origin,
        "Content-Type": "application/json",
        ...(cookie ? { Cookie: cookie } : {}),
      },
      body: JSON.stringify(data),
    });
  try {
    assert.equal(
      (await post("dav", { method: "GET", path: "books/a/main.md" })).status,
      401,
    );
    assert.equal(
      (
        await post(
          "login",
          { user: "vanong", password: "service-password" },
          "https://evil.test",
        )
      ).status,
      403,
    );
    assert.equal(
      (await post("login", { user: "vanong", password: "wrong" })).status,
      401,
    );
    assert.equal(
      (
        await post("setup", {
          token: "invalid",
          user: "vanong",
          password: "service-password",
        })
      ).status,
      403,
    );
    const setup = await post("setup", {
      token,
      user: "vanong",
      password: "service-password",
    });
    assert.equal(setup.status, 200);
    assert.equal(
      calls.length,
      0,
      "account setup must never call another identity service",
    );
    assert.equal(
      (
        await post("setup", {
          token,
          user: "vanong",
          password: "service-password",
        })
      ).status,
      403,
    );
    const login = await post("login", {
      user: "vanong",
      password: "service-password",
    });
    assert.equal(login.status, 200);
    assert.equal(
      calls.length,
      0,
      "login must never call another identity service",
    );
    cookie = login.headers.get("set-cookie").split(";")[0];
    assert.equal((await login.json()).account, null);
    const configured = await post("configure", {
      url: library,
      user: "vanong",
      password: "private-nextcloud-app-password",
    });
    assert.equal(configured.status, 200);
    const profile = await configured.json();
    assert(profile.account);
    assert(!JSON.stringify(profile).includes("password"));
    const database = await readFile(configFile + ".sqlite");
    assert.equal(database.subarray(0, 16).toString(), "SQLite format 3\0");
    assert(!database.includes(Buffer.from("service-password")));
    const encrypted = await readFile(configFile, "utf8");
    assert(!encrypted.includes("private-nextcloud-app-password"));
    assert(!encrypted.includes("service-password"));
    assert.equal(
      (
        await post("dav", {
          method: "GET",
          path: "books/a/main.md",
          account: "another",
        })
      ).status,
      409,
    );
    assert.equal(
      (
        await post("dav", {
          method: "PUT",
          path: "books/a/main.md",
          data: "",
          account: profile.account,
        })
      ).status,
      400,
    );
    assert.equal(
      (
        await post("dav", {
          method: "PUT",
          path: "books/a/main.md",
          data: "YWJj",
          headers: { "If-Match": '"1"' },
          account: profile.account,
        })
      ).status,
      200,
    );
    assert.equal(calls.at(-1).options.headers["If-Match"], '"1"');
    assert.equal(calls.at(-1).options.body.toString(), "abc");
    const forwarded = await post("dav", {
      method: "GET",
      path: "books/a/main.md",
      account: profile.account,
    });
    assert.equal((await forwarded.json()).retryAfter, "9");
    assert.equal(
      calls.at(-1).options.headers.Authorization,
      "Basic " +
        Buffer.from("vanong:private-nextcloud-app-password").toString("base64"),
    );
    const restarted = createNotesServer({
      origin: "http://notes.test",
      davOrigin: "https://cloud.test",
      secret: "test-secret-with-at-least-32-characters",
      configFile,
      secure: false,
      fetcher,
    });
    await new Promise((resolve) => restarted.listen(0, "127.0.0.1", resolve));
    const response = await fetch(
      "http://127.0.0.1:" + restarted.address().port + "/api/session",
      { headers: { Cookie: cookie } },
    );
    assert.equal((await response.json()).account, profile.account);
    assert.equal((await post("logout", {})).status, 200);
    assert.equal(
      (await fetch(base + "/api/session", { headers: { Cookie: cookie } }))
        .status,
      401,
    );
    const beforeReset = await post("login", {
      user: "vanong",
      password: "service-password",
    });
    cookie = beforeReset.headers.get("set-cookie").split(";")[0];
    assert.equal(
      (await fetch(base + "/api/session", { headers: { Cookie: cookie } }))
        .status,
      200,
    );
    const resetDB = new Accounts(configFile + ".sqlite");
    const resetToken = resetDB.invite();
    resetDB.close();
    const reset = await post("setup", {
      token: resetToken,
      user: "vanong",
      password: "new-independent-password",
    });
    assert.equal(reset.status, 200);
    assert.equal(
      (
        await fetch(
          "http://127.0.0.1:" + restarted.address().port + "/api/session",
          { headers: { Cookie: cookie } },
        )
      ).status,
      401,
    );
    assert.equal(
      (await post("login", { user: "vanong", password: "service-password" }))
        .status,
      401,
    );
    restarted.closeAllConnections();
    await new Promise((resolve) => restarted.close(resolve));
  } finally {
    server.closeAllConnections();
    await new Promise((resolve) => server.close(resolve));
    await rm(dir, { recursive: true, force: true });
  }
});
