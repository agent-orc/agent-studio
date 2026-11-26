// 3D deploy landscape with three.js (WebGL): one instanced box per week and project,
// height = deploy runs in that week, colour = share of failed runs. Orthographic camera, two lights.
import { WebGLRenderer, Scene, OrthographicCamera, InstancedMesh, BoxGeometry, MeshLambertMaterial, DirectionalLight, HemisphereLight, Object3D, Color, Group } from 'three';

export function mount3d(el, land, ctx) {
  const t = ctx.theme, W = land.weeks, P = land.projects.length, w = el.clientWidth || 800, h = el.clientHeight || 480;
  const renderer = new WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2)); renderer.setSize(w, h); renderer.setClearColor(new Color(t.ground));
  el.appendChild(renderer.domElement);
  const scene = new Scene(), group = new Group(); scene.add(group);
  const span = Math.max(W, P) * 0.62, aspect = w / h;
  const camera = new OrthographicCamera(-span * aspect, span * aspect, span, -span, -200, 400);
  camera.position.set(60, 55, 60); camera.lookAt(0, 4, 0);
  const key = new DirectionalLight(0xffffff, 2.2); key.position.set(30, 60, 20); scene.add(key);
  scene.add(new HemisphereLight(0xffffff, new Color(t.ground), 1.1));
  const geo = new BoxGeometry(0.82, 1, 0.82); geo.translate(0, 0.5, 0);
  const mesh = new InstancedMesh(geo, new MeshLambertMaterial(), W * P), o = new Object3D();
  const low = new Color(t.ramp[2]), high = new Color(t.bad), c = new Color();
  for (let wk = 0; wk < W; wk++) for (let p = 0; p < P; p++) {
    const i = wk * P + p, n = land.count[i];
    o.position.set(wk - W / 2, 0, p - P / 2); o.scale.set(1, Math.max(0.04, 14 * n / (land.max || 1)), 1); o.updateMatrix();
    mesh.setMatrixAt(i, o.matrix); mesh.setColorAt(i, c.copy(low).lerp(high, Math.min(1, (land.fail[i] || 0) * 2)));
  }
  group.add(mesh);
  const render = () => renderer.render(scene, camera);
  render();
  return {
    rotate(d) { group.rotation.y += d; render(); },
    dispose() { geo.dispose(); mesh.material.dispose(); renderer.dispose(); renderer.domElement.remove(); },
  };
}
