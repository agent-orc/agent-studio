// Synthetic scene for the renderer benchmark: 10 sites, 40 machines and 450
// containers (500 nodes) and 2,000 links, 70 % inside a site. Deterministic.
export function synthScene(seed = 3955) {
  let s = seed >>> 0;
  const rnd = () => { s = (s * 1664525 + 1013904223) >>> 0; return s / 4294967296; };
  const nodes = [];
  const containers = [];
  const bySite = [];
  for (let i = 0; i < 10; i++) {
    const site = { id: `s${i}`, kind: 'site', label: `Site ${i + 1}`, kicker: 'Site', row: i === 5 };
    nodes.push(site);
    bySite.push([]);
    for (let j = 0; j < 4; j++) {
      const m = { id: `s${i}m${j}`, kind: 'machine', parent: site.id, label: `Machine ${i + 1}.${j + 1}`, sub: 'Cloud server' };
      nodes.push(m);
      const count = j < 1 ? 12 : 11 - (j === 3 && i % 2 ? 1 : 0);
      for (let c = 0; c < count && containers.length < 450; c++) {
        const n = { id: `${m.id}c${c}`, kind: 'container', parent: m.id, label: `service-${i}${j}${c}`, sub: 'container' };
        nodes.push(n); containers.push(n); bySite[i].push(n);
      }
    }
  }
  while (containers.length < 450) {
    const n = { id: `extra${containers.length}`, kind: 'container', parent: 's9m3', label: `service-x${containers.length}`, sub: 'container' };
    nodes.push(n); containers.push(n); bySite[9].push(n);
  }
  const kinds = ['private', 'tunnel', 'forwarder', 'cdn', 'https'];
  const links = [];
  for (let i = 0; i < 2000; i++) {
    const local = rnd() < 0.7;
    const si = Math.floor(rnd() * 10);
    const pool = bySite[si];
    const a = pool[Math.floor(rnd() * pool.length)];
    const pool2 = local ? pool : bySite[Math.floor(rnd() * 10)];
    let b = pool2[Math.floor(rnd() * pool2.length)];
    if (b === a) b = pool2[(pool2.indexOf(a) + 1) % pool2.length];
    links.push({ id: `l${i}`, from: a.id, to: b.id, kind: kinds[i % 5], traffic: Math.round(rnd() * 1000), latencyMs: Math.round(rnd() * 80) });
  }
  return { nodes, links };
}
