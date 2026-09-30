// Generates icons/icon{16,48,128}.png: a blue shield with a white check mark.
// Pure Node (zlib only), no native dependencies.
import { deflateSync } from "node:zlib";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const outDir = join(here, "..", "icons");

const crcTable = new Uint32Array(256).map((_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
function crc32(buf) {
  let c = 0xffffffff;
  for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(td));
  return Buffer.concat([len, td, crc]);
}
function png(size, rgbaAt) {
  const raw = Buffer.alloc((size * 4 + 1) * size);
  for (let y = 0; y < size; y++) {
    raw[y * (size * 4 + 1)] = 0; // filter: none
    for (let x = 0; x < size; x++) {
      const [r, g, b, a] = rgbaAt(x, y);
      raw.set([r, g, b, a], y * (size * 4 + 1) + 1 + x * 4);
    }
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr),
    chunk("IDAT", deflateSync(raw, { level: 9 })),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

// Shield outline in normalized coords: x in [-1,1], y in [0,1] (top to bottom).
// Straight sides down to y=0.45, then a rounded taper to a point at y=1.
function shieldHalfWidth(y) {
  if (y < 0) return 0;
  if (y <= 0.45) return 1;
  if (y > 1) return 0;
  const t = (y - 0.45) / 0.55;
  return Math.sqrt(Math.max(0, 1 - t * t));
}
function distToSegment(px, py, ax, ay, bx, by) {
  const dx = bx - ax, dy = by - ay;
  const t = Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)));
  return Math.hypot(px - (ax + t * dx), py - (ay + t * dy));
}

const BLUE = [31, 95, 191];
const WHITE = [255, 255, 255];

function shield(size) {
  const ss = 4; // supersampling per axis for smooth edges
  return png(size, (x, y) => {
    let cover = 0, check = 0;
    for (let i = 0; i < ss; i++) {
      for (let j = 0; j < ss; j++) {
        const px = (x + (i + 0.5) / ss) / size;
        const py = (y + (j + 0.5) / ss) / size;
        const nx = (px - 0.5) / 0.44; // shield spans 6%..94% horizontally
        const ny = (py - 0.06) / 0.88; // and vertically
        if (Math.abs(nx) <= shieldHalfWidth(ny)) {
          cover++;
          const d = Math.min(
            distToSegment(nx, ny, -0.45, 0.48, -0.12, 0.72),
            distToSegment(nx, ny, -0.12, 0.72, 0.5, 0.22),
          );
          if (d < 0.14) check++;
        }
      }
    }
    const a = Math.round((cover / (ss * ss)) * 255);
    if (a === 0) return [0, 0, 0, 0];
    const w = check / Math.max(cover, 1);
    const mix = (i) => Math.round(BLUE[i] * (1 - w) + WHITE[i] * w);
    return [mix(0), mix(1), mix(2), a];
  });
}

mkdirSync(outDir, { recursive: true });
for (const size of [16, 48, 128]) {
  writeFileSync(join(outDir, `icon${size}.png`), shield(size));
}
console.log("icons written to", outDir);
