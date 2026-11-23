import { DatabaseSync } from "node:sqlite";
import { mkdirSync, chmodSync } from "node:fs";
import { dirname } from "node:path";
import { randomBytes, createHash, scrypt, timingSafeEqual } from "node:crypto";
import { promisify } from "node:util";

const derive = promisify(scrypt);
const hashToken = (value) => createHash("sha256").update(value).digest("hex");
const error = (status, message) =>
  Object.assign(new Error(message), { status });
export class Accounts {
  constructor(path) {
    mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
    this.db = new DatabaseSync(path);
    chmodSync(path, 0o600);
    this.db.exec(`PRAGMA busy_timeout=5000; PRAGMA journal_mode=DELETE;
      CREATE TABLE IF NOT EXISTS account (
        id INTEGER PRIMARY KEY CHECK(id=1), username TEXT NOT NULL,
        salt TEXT NOT NULL, password_hash TEXT NOT NULL, version TEXT NOT NULL
      ) STRICT;
      CREATE TABLE IF NOT EXISTS invitation (
        id INTEGER PRIMARY KEY CHECK(id=1), token_hash TEXT NOT NULL, expires INTEGER NOT NULL
      ) STRICT;`);
  }
  current() {
    return this.db
      .prepare("SELECT username, version FROM account WHERE id=1")
      .get();
  }
  invite() {
    const token = randomBytes(32).toString("base64url");
    this.db
      .prepare("INSERT OR REPLACE INTO invitation VALUES (1, ?, ?)")
      .run(hashToken(token), Date.now() + 86400000);
    return token;
  }
  async passwordHash(password, salt) {
    return Buffer.from(
      await derive(password, salt, 64, {
        N: 32768,
        r: 8,
        p: 1,
        maxmem: 64 * 1024 * 1024,
      }),
    );
  }
  async verify(username, password) {
    const row = this.db.prepare("SELECT * FROM account WHERE id=1").get();
    const candidate = await this.passwordHash(
      password,
      row?.salt || "no-account-timing-salt",
    );
    const expected = row
      ? Buffer.from(row.password_hash, "hex")
      : Buffer.alloc(64);
    return row &&
      timingSafeEqual(candidate, expected) &&
      row.username === username
      ? { username, version: row.version }
      : null;
  }
  async setup(token, username, password) {
    if (!/^[A-Za-z0-9_.@-]{1,64}$/.test(username))
      throw error(400, "用户名请使用 1–64 个字母、数字或 _ . @ -");
    if (password.length < 12 || password.length > 512)
      throw error(400, "密码需要 12–512 个字符");
    const invitation = this.db
      .prepare("SELECT * FROM invitation WHERE id=1")
      .get();
    if (
      !invitation ||
      invitation.expires < Date.now() ||
      invitation.token_hash !== hashToken(token)
    )
      throw error(403, "创建/重设链接无效或已过期");
    const salt = randomBytes(16).toString("hex"),
      version = randomBytes(24).toString("hex");
    const hash = (await this.passwordHash(password, salt)).toString("hex");
    this.db.exec("BEGIN IMMEDIATE");
    try {
      // Recheck after the asynchronous password derivation: an invitation is single use.
      const fresh = this.db
        .prepare("SELECT * FROM invitation WHERE id=1")
        .get();
      if (
        !fresh ||
        fresh.expires < Date.now() ||
        fresh.token_hash !== invitation.token_hash
      )
        throw error(403, "创建/重设链接无效或已过期");
      this.db
        .prepare("INSERT OR REPLACE INTO account VALUES (1, ?, ?, ?, ?)")
        .run(username, salt, hash, version);
      this.db.prepare("DELETE FROM invitation WHERE id=1").run();
      this.db.exec("COMMIT");
    } catch (e) {
      this.db.exec("ROLLBACK");
      throw e;
    }
    return { username, version };
  }
  close() {
    this.db.close();
  }
}
