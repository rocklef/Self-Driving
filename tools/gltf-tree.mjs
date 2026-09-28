// Prints the node hierarchy of a .gltf file (names, mesh flags), to find vehicles and wheels.
// Usage: node tools/gltf-tree.mjs <file.gltf> [maxDepth]
import fs from "node:fs";

const [file, maxDepth = "6"] = process.argv.slice(2);
const g = JSON.parse(fs.readFileSync(file, "utf8"));
const scene = g.scenes[g.scene ?? 0];

function walk(i, depth) {
  const n = g.nodes[i];
  if (depth > Number(maxDepth)) return;
  const t = n.translation ? ` t=[${n.translation.map((v) => v.toFixed(2)).join(",")}]` : "";
  console.log(`${"  ".repeat(depth)}${n.name ?? "(node " + i + ")"}${n.mesh !== undefined ? " [mesh]" : ""}${t}`);
  for (const c of n.children ?? []) walk(c, depth + 1);
}
for (const r of scene.nodes) walk(r, 0);
