const motionSystemPreference = matchMedia('(prefers-reduced-motion: no-preference)');
const activeMotions = new Map();
let motionMode = 'on';

function motionEnabled() {
  return motionMode === 'on' || (motionMode === 'system' && motionSystemPreference.matches);
}

function applyMotionPreference(mode = motionMode) {
  motionMode = mode;
  const enabled = motionEnabled();
  document.documentElement.dataset.motion = enabled ? 'on' : 'off';
  if (!enabled) {
    for (const animation of activeMotions.values()) animation.cancel();
    activeMotions.clear();
  }
}

motionSystemPreference.addEventListener('change', () => applyMotionPreference());

function cancelMotion(node) {
  activeMotions.get(node)?.cancel();
  activeMotions.delete(node);
}

function animateMotion(node, keyframes, timing) {
  if (!motionEnabled() || !node.getClientRects().length) return;
  cancelMotion(node);
  const animation = node.animate(keyframes, { easing: 'cubic-bezier(.22, .7, .24, 1)', ...timing });
  activeMotions.set(node, animation);
  const clear = () => {
    if (activeMotions.get(node) === animation) activeMotions.delete(node);
  };
  animation.onfinish = clear;
  animation.oncancel = clear;
}

function slideIn(node, x, y, duration = 235) {
  animateMotion(node, [
    { translate: x + 'px ' + y + 'px', opacity: .55 },
    { translate: '0px 0px', opacity: 1 },
  ], { duration });
}

function visibleMotionRows(list, rows) {
  if (!motionEnabled() || !rows.length || !list.getClientRects().length) return [];
  const bounds = document.getElementById('main').getBoundingClientRect();
  const visible = [];
  for (const row of rows) {
    const rect = row.getBoundingClientRect();
    if (!rect.height || rect.bottom <= bounds.top) continue;
    if (rect.top >= bounds.bottom && !activeMotions.has(row)) break;
    if (rect.top >= bounds.bottom) continue;
    visible.push({ row, top: rect.top });
    if (visible.length >= 12) break;
  }
  return visible;
}

function captureRowPositions(list) {
  return new Map(visibleMotionRows(list, list.children).map(({ row, top }) => [row.dataset.motionKey, top]));
}

function animateVisibleRows(list, rows, previous = new Map()) {
  let revealIndex = 0;
  for (const { row, top } of visibleMotionRows(list, rows)) {
    const oldTop = previous.get(row.dataset.motionKey);
    if (oldTop !== undefined) {
      const offset = oldTop - top;
      if (Math.abs(offset) > 1) animateMotion(row, [{ translate: '0px ' + offset + 'px' }, { translate: '0px 0px' }], { duration: 260 });
    } else {
      animateMotion(row, [{ opacity: 0, translate: '0px 8px' }, { opacity: 1, translate: '0px 0px' }],
        { duration: 185, delay: Math.min(revealIndex++ * 20, 100), fill: 'backwards' });
    }
  }
}
