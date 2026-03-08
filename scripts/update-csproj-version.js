/**
 * package.json のバージョンを .csproj に同期するスクリプト。
 *
 * changeset version 実行後に呼ばれ、package.json を single source of truth として
 * .csproj の <Version> タグを更新する。
 * これにより VPM パッケージと NuGet パッケージのバージョンが常に一致する。
 */

const fs = require("fs");
const path = require("path");

const packageJson = JSON.parse(
  fs.readFileSync(path.join(__dirname, "..", "package.json"), "utf8")
);
const version = packageJson.version;

const csprojPath = path.join(
  __dirname,
  "..",
  "Tools~",
  "NetworkIDChecker",
  "NetworkIDChecker.csproj"
);
let csproj = fs.readFileSync(csprojPath, "utf8");

csproj = csproj.replace(
  /<Version>.*<\/Version>/,
  `<Version>${version}</Version>`
);

fs.writeFileSync(csprojPath, csproj, "utf8");

console.log(`Updated .csproj version to ${version}`);
