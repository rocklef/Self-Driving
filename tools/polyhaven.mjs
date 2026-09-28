// Downloads CC0 assets from Poly Haven (https://polyhaven.com) into the Unity project.
// Usage: node tools/polyhaven.mjs
import fs from "node:fs/promises";
import path from "node:path";

const OUT = "UnityProject/Assets/PolyHaven";
const MODELS = [
  "street_lamp_01", "fire_hydrant", "modular_street_seating", "planter_box_01",
  "concrete_road_barrier", "concrete_road_barrier_02", "covered_car",
  "plastic_crate_01", "cardboard_box_01", "Barrel_01", "WetFloorSign_01",
  "island_tree_01", "island_tree_02",
];
const TEXTURES = ["asphalt_02", "concrete_pavement_02"];
const HDRI = "kloofendal_48d_partly_cloudy_puresky";

async function json(url) {
  const r = await fetch(url);
  if (!r.ok) throw new Error(`${url}: HTTP ${r.status}`);
  return r.json();
}

async function download(url, file) {
  try { await fs.access(file); return 0; } catch {}
  let buf;
  for (let attempt = 1; ; attempt++) {
    try {
      const r = await fetch(url);
      if (!r.ok) throw new Error(`${url}: HTTP ${r.status}`);
      buf = Buffer.from(await r.arrayBuffer());
      break;
    } catch (e) {
      if (attempt >= 5) throw e;
      await new Promise((ok) => setTimeout(ok, 1500 * attempt));
    }
  }
  await fs.mkdir(path.dirname(file), { recursive: true });
  await fs.writeFile(file, buf);
  return buf.length;
}

let total = 0;

// Ground texture for the grass areas.
const grass = "leafy_grass";
TEXTURES.push(grass);

for (const id of MODELS) {
  const files = await json(`https://api.polyhaven.com/files/${id}`);
  const g = files.gltf["1k"].gltf;
  total += await download(g.url, `${OUT}/models/${id}/${id}.gltf`);
  for (const [rel, f] of Object.entries(g.include)) total += await download(f.url, `${OUT}/models/${id}/${rel}`);
  console.log("model  ", id);
}

for (const id of TEXTURES) {
  const files = await json(`https://api.polyhaven.com/files/${id}`);
  for (const map of ["Diffuse", "nor_gl", "Rough"]) {
    const f = files[map]?.["2k"]?.jpg ?? files[map]?.["2k"]?.png;
    if (f) total += await download(f.url, `${OUT}/textures/${id}/${id}_${map}${path.extname(f.url)}`);
  }
  console.log("texture", id);
}

const h = (await json(`https://api.polyhaven.com/files/${HDRI}`)).hdri["2k"].hdr;
total += await download(h.url, `${OUT}/hdri/${HDRI}.hdr`);
console.log("hdri   ", HDRI);
console.log(`done, ${(total / 1e6).toFixed(1)} MB downloaded`);
