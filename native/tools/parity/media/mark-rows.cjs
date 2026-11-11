// Mark rows of docs/parity/bar.md: node mark-rows.cjs <marks.json>, where marks.json is {"BAR-751": "tested: ...", ...}.
// Replaces the last cell (the Native column) of each named row; prints how many rows changed. Lane native-r7-media.
'use strict';
const fs = require('fs');
const path = require('path');
const file = path.join(__dirname, '..', '..', '..', 'docs', 'parity', 'bar.md');
const marks = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
let n = 0;
const out = fs.readFileSync(file, 'utf8').split('\n').map((line) => {
  const m = /^\| (BAR-\d+) \|/.exec(line);
  if (!m || !(m[1] in marks)) return line;
  const cut = line.replace(/\s*\|\s*$/, '').lastIndexOf('|');
  n++;
  return `${line.slice(0, cut)}| ${marks[m[1]]} |`;
});
fs.writeFileSync(file, out.join('\n'));
const missing = Object.keys(marks).length - n;
console.log(`marked ${n} rows${missing ? `, ${missing} not found` : ''}`);
