import { Accounts } from "./accounts.mjs";
const file =
  process.env.NOTES_ACCOUNTS_FILE ||
  (process.env.NOTES_CONFIG_FILE || "notes-data/settings.enc") + ".sqlite";
if (process.argv[2] !== "invite")
  throw Error("Usage: node server/manage-account.mjs invite");
const accounts = new Accounts(file);
try {
  const url = new URL(
    process.env.NOTES_ORIGIN || "https://notes.example.com",
  );
  url.pathname = "/api/account-setup";
  url.hash = "setup=" + accounts.invite();
  console.log(url.href);
} finally {
  accounts.close();
}
