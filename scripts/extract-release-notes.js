const fs = require('fs');

const version = process.argv[2];
if (!version) {
  console.error('Usage: node extract-release-notes.js <version>');
  process.exit(1);
}

// 改行をLFに統一
const changelog = fs.readFileSync('CHANGELOG.md', 'utf8').replace(/\r\n/g, '\n');

// バージョンセクションを抽出
const lines = changelog.split('\n');
let inSection = false;
let notes = [];

for (const line of lines) {
  if (line.startsWith('## ')) {
    if (line === `## ${version}`) {
      inSection = true;
      continue; // ヘッダー行はスキップ
    } else if (inSection) {
      break; // 次のセクションに到達
    }
  }
  if (inSection) {
    notes.push(line);
  }
}

console.log(notes.join('\n').trim());
