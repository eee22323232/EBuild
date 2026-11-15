function boxFaces([x, y, z, width, height, depth], [u, v], mirror = false, cape = false) {
  const [right, bottom, front] = [x + width, y + height, z + depth];
  const [w, h, d] = [Math.round(width), Math.round(height), Math.round(depth)];
  const faces = [
    { normal: [0, 0, 1], corners: [[x, y, front], [right, y, front], [x, bottom, front]], rect: [u + d, v + d, w, h] },
    { normal: [0, 0, -1], corners: [[right, y, z], [x, y, z], [right, bottom, z]], rect: [u + 2 * d + w, v + d, w, h] },
    { normal: [-1, 0, 0], corners: [[x, y, z], [x, y, front], [x, bottom, z]], rect: [u, v + d, d, h] },
    { normal: [1, 0, 0], corners: [[right, y, front], [right, y, z], [right, bottom, front]], rect: [u + d + w, v + d, d, h] },
    { normal: [0, -1, 0], corners: [[x, y, z], [right, y, z], [x, y, front]], rect: [u + d, v, w, d] },
    { normal: [0, 1, 0], corners: [[x, bottom, front], [right, bottom, front], [x, bottom, z]], rect: [u + d + w, v, w, d] },
  ];
  if (mirror) {
    [faces[2].rect, faces[3].rect] = [faces[3].rect, faces[2].rect];
    for (const face of faces) face.mirror = true;
  }
  if (cape) [faces[0].rect, faces[1].rect] = [faces[1].rect, faces[0].rect];
  return faces;
}

const playerHead = { box: [-4, -16, -4, 8, 8, 8], uv: [0, 0], overlay: [32, 0] };

function playerParts(slim, legacy) {
  const arm = slim && !legacy ? 3 : 4;
  return [
    playerHead,
    { box: [-4, -8, -2, 8, 12, 4], uv: [16, 16], overlay: [16, 32] },
    { box: [-4 - arm, -8, -2, arm, 12, 4], uv: [40, 16], overlay: [40, 32] },
    { box: [4, -8, -2, arm, 12, 4], uv: legacy ? [40, 16] : [32, 48], overlay: [48, 48], mirror: legacy },
    { box: [-4, 4, -2, 4, 12, 4], uv: [0, 16], overlay: [0, 32] },
    { box: [0, 4, -2, 4, 12, 4], uv: legacy ? [0, 16] : [16, 48], overlay: [0, 48], mirror: legacy },
  ];
}

function defaultPlayerSkin() {
  const image = document.createElement('canvas');
  image.width = image.height = 64;
  const ctx = image.getContext('2d');
  const colors = ['#bc865f', '#4b8f83', '#bc865f', '#bc865f', '#3f506f', '#3f506f'];
  playerParts(false, false).forEach(({ box, uv }, index) => {
    for (const { rect: [x, y, w, h] } of boxFaces(box, uv)) {
      ctx.fillStyle = colors[index];
      ctx.fillRect(x, y, w, h);
      if (index === 0) {
        ctx.fillStyle = '#554139';
        ctx.fillRect(x, y, w, y === 0 ? h : 2);
      } else if (index === 2 || index === 3) {
        ctx.fillStyle = colors[1];
        ctx.fillRect(x, y, w, 4);
      } else if (index >= 4) {
        ctx.fillStyle = '#303a4a';
        ctx.fillRect(x, y + h - 2, w, 2);
      }
    }
  });
  ctx.fillStyle = '#f8ede0';
  ctx.fillRect(9, 12, 2, 1);
  ctx.fillRect(13, 12, 2, 1);
  ctx.fillStyle = '#465971';
  ctx.fillRect(10, 12, 1, 1);
  ctx.fillRect(13, 12, 1, 1);
  ctx.fillStyle = '#7f5245';
  ctx.fillRect(11, 14, 2, 1);
  return image;
}

function loadPlayerTexture(url) {
  if (!url) return Promise.resolve(null);
  const image = new Image();
  image.src = url;
  return image.decode().then(() => image, () => null);
}

function skinLayers(part, skin) {
  const layers = [{ box: part.box, uv: part.uv, texture: skin, mirror: part.mirror }];
  if (skin.height !== 32 || part.overlay[1] === 0) {
    layers.push({ box: part.box, uv: part.overlay, texture: skin, margin: part.overlay[1] === 0 ? .4 : .22 });
  }
  return layers;
}

function drawPlayerParts(canvas, parts, yaw, pitch, scale, shadow = false) {
  const ctx = canvas.getContext('2d');
  const center = canvas.width / 2;
  const cosY = Math.cos(yaw), sinY = Math.sin(yaw);
  const cosP = Math.cos(pitch), sinP = Math.sin(pitch);
  function project([x, y, z]) {
    const depth = z * cosY - x * sinY;
    return [(x * cosY + z * sinY) * scale, (y * cosP - depth * sinP) * scale, y * sinP + depth * cosP];
  }
  const faces = [];
  for (const { box, uv, texture, mirror, margin = 0, cape } of parts) {
    for (const face of boxFaces(box, uv, mirror, cape)) {
      if (project(face.normal)[2] <= 0) continue;
      const corners = face.corners.map(point => {
        const [x, y, z] = point.map((value, index) => value + (value === box[index] ? -margin : margin));
        return project([x, y, cape ? z - (y + 8) * .16 : z]);
      });
      const depth = (corners[1][2] + corners[2][2]) / 2;
      faces.push({ ...face, corners, depth, texture });
    }
  }
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  if (shadow) {
    ctx.fillStyle = '#76829d2e';
    ctx.beginPath();
    ctx.ellipse(center, canvas.height * .916, scale * 7, scale * .9, 0, 0, Math.PI * 2);
    ctx.fill();
  }
  ctx.imageSmoothingEnabled = false;
  for (const { corners: [a, b, c], rect: [u, v, w, h], mirror, texture } of faces.sort((a, b) => a.depth - b.depth)) {
    ctx.setTransform((b[0] - a[0]) / w, (b[1] - a[1]) / w,
      (c[0] - a[0]) / h, (c[1] - a[1]) / h, a[0] + center, a[1] + canvas.height / 2);
    if (mirror) { ctx.translate(w, 0); ctx.scale(-1, 1); }
    const density = texture.width / 64;
    ctx.drawImage(texture, u * density, v * density, w * density, h * density, 0, 0, w, h);
  }
  ctx.resetTransform();
}

class PlayerPreview {
  constructor(canvas, avatar) {
    this.canvas = canvas;
    this.avatar = avatar;
    this.fallback = defaultPlayerSkin();
    this.skin = this.fallback;
    this.yaw = -.36;
    this.pitch = -.13;
    this.frame = 0;
    this.generation = 0;
    let drag = null;
    canvas.addEventListener('pointerdown', event => {
      if (!event.isPrimary || event.button !== 0) return;
      drag = [event.clientX, event.clientY];
      canvas.setPointerCapture(event.pointerId);
    });
    canvas.addEventListener('pointermove', event => {
      if (!event.isPrimary || !drag) return;
      this.rotate((event.clientX - drag[0]) * .018, (event.clientY - drag[1]) * .012);
      drag = [event.clientX, event.clientY];
    });
    canvas.addEventListener('lostpointercapture', () => { drag = null; });
    canvas.addEventListener('keydown', event => {
      const turns = { ArrowLeft: [-.18, 0], ArrowRight: [.18, 0], ArrowUp: [0, -.12], ArrowDown: [0, .12] };
      if (!turns[event.key]) return;
      event.preventDefault();
      this.rotate(...turns[event.key]);
    });
    this.setTextures({});
  }

  rotate(x, y) {
    this.yaw += x;
    this.pitch = Math.max(-.65, Math.min(.65, this.pitch + y));
    this.queueDraw();
  }

  turnTo(back) {
    this.yaw = back ? Math.PI - .3 : -.36;
    this.pitch = -.13;
    this.queueDraw();
  }

  async setTextures({ skin = '', cape = '', variant = 'classic' }) {
    const key = [skin, cape, variant];
    if (this.textures?.every((value, index) => value === key[index])) return;
    this.textures = key;
    const generation = ++this.generation;
    this.skin = this.fallback;
    this.cape = null;
    this.slim = false;
    this.drawAvatar();
    this.queueDraw();
    const [skinImage, capeImage] = await Promise.all([loadPlayerTexture(skin), loadPlayerTexture(cape)]);
    if (this.generation !== generation) return;
    if (skinImage?.width === 64 && [32, 64].includes(skinImage.height)) this.skin = skinImage;
    if (capeImage?.width >= 64 && capeImage.width === capeImage.height * 2) this.cape = capeImage;
    this.slim = this.skin !== this.fallback && variant === 'slim' && this.skin.height === 64;
    this.drawAvatar();
    this.queueDraw();
  }

  drawAvatar() {
    const { width, height } = this.avatar;
    const ctx = this.avatar.getContext('2d');
    ctx.imageSmoothingEnabled = false;
    ctx.clearRect(0, 0, width, height);
    const faceSize = Math.min(width, height) - 8;
    const outerSize = Math.min(width, height) - 2;
    ctx.drawImage(this.skin, 8, 8, 8, 8, (width - faceSize) / 2, (height - faceSize) / 2, faceSize, faceSize);
    ctx.drawImage(this.skin, 40, 8, 8, 8, (width - outerSize) / 2, (height - outerSize) / 2, outerSize, outerSize);
  }

  queueDraw() {
    if (this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.draw();
    });
  }

  draw() {
    const legacy = this.skin.height === 32;
    const parts = playerParts(this.slim, legacy).flatMap(part => skinLayers(part, this.skin));
    if (this.cape) parts.push({ box: [-5, -8, -3.6, 10, 16, 1], uv: [0, 0], texture: this.cape, cape: true });
    const scale = Math.min(this.canvas.width / 25, this.canvas.height / 40);
    drawPlayerParts(this.canvas, parts, this.yaw, this.pitch, scale, true);
  }
}
