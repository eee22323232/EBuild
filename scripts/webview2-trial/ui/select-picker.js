const selectTriggers = new WeakMap();
const selectPopover = el('select-popover');
const selectSearch = el('select-search');
const selectResults = el('select-results');
let openSelect;
let detachSelectTimer;

function selectLabel(select) {
  const label = select.labels[0];
  return select.getAttribute('aria-label') || el(select.getAttribute('aria-labelledby'))?.textContent.trim() ||
    (label?.querySelector('strong') || label)?.textContent.trim() || '选择';
}

function attachSelectPicker(select) {
  if (selectTriggers.has(select) || select.multiple || select.size > 1) return;
  const trigger = element('button', undefined, 'select-trigger');
  trigger.type = 'button';
  trigger.setAttribute('aria-haspopup', 'listbox');
  trigger.setAttribute('aria-controls', selectPopover.id);
  trigger.setAttribute('popovertarget', selectPopover.id);
  trigger.setAttribute('aria-expanded', 'false');
  const chevron = element('span', undefined, 'select-chevron');
  chevron.setAttribute('aria-hidden', 'true');
  trigger.append(element('span'), chevron);
  trigger.addEventListener('click', event => {
    event.preventDefault();
    if (openSelect?.select === select) closeSelectPicker(true);
    else openSelectPicker(select);
  });
  trigger.addEventListener('keydown', event => {
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
    event.preventDefault();
    openSelectPicker(select);
  });
  select.addEventListener('change', () => updateSelectPicker(select));
  select.addEventListener('invalid', event => {
    event.preventDefault();
    trigger.focus();
  });
  for (const label of select.labels) {
    label.addEventListener('click', event => {
      event.preventDefault();
      trigger.click();
    });
  }
  select.after(trigger);
  select.dataset.enhancedSelect = '';
  selectTriggers.set(select, trigger);
  updateSelectPicker(select);
}

function updateSelectPicker(select) {
  const trigger = selectTriggers.get(select);
  if (!trigger) return;
  const option = select.selectedOptions[0];
  const label = option?.label || '选择选项';
  trigger.firstElementChild.textContent = label;
  trigger.setAttribute('aria-label', selectLabel(select) + '：' + label);
  trigger.disabled = select.disabled || !select.length || (select.required && !select.value);
  trigger.hidden = select.hidden;
  if (openSelect?.select === select) {
    if (trigger.disabled || trigger.hidden) closeSelectPicker();
    else renderSelectOptions();
  }
}

function openSelectPicker(select) {
  const trigger = selectTriggers.get(select);
  if (!trigger || trigger.matches(':disabled') || openSelect?.select === select) return;
  closeSelectPicker();
  const searchable = select.hasAttribute('data-searchable') || select.length > 10;
  const focusTarget = searchable ? selectSearch : selectResults;
  const label = selectLabel(select);
  openSelect = { select, trigger, focusTarget, searchable, rows: [], active: -1 };
  const placeholder = (searchable && select.id === 'install-version') ? '搜索版本，例如 1.21' : '搜索选项';
  selectSearch.value = '';
  selectSearch.hidden = !searchable;
  selectResults.tabIndex = searchable ? -1 : 0;
  selectSearch.placeholder = placeholder;
  selectSearch.setAttribute('aria-label', placeholder);
  selectResults.setAttribute('aria-label', label);
  selectPopover.setAttribute('aria-label', label);
  selectPopover.classList.toggle('is-searchable', searchable);
  trigger.after(selectPopover);
  trigger.setAttribute('aria-expanded', 'true');
  placeSelectPopover();
  renderSelectOptions();
  selectPopover.showPopover({ source: trigger });
  focusTarget.focus({ preventScroll: true });
  highlightSelectOption(openSelect.active);
}

function closeSelectPicker(restoreFocus = false) {
  if (!openSelect) return;
  const { trigger } = openSelect;
  if (selectPopover.matches(':popover-open')) selectPopover.hidePopover();
  else clearSelectPicker();
  if (restoreFocus && trigger.isConnected) trigger.focus({ preventScroll: true });
}

function clearSelectPicker() {
  if (!openSelect) return;
  openSelect.trigger.setAttribute('aria-expanded', 'false');
  openSelect.focusTarget.removeAttribute('aria-activedescendant');
  openSelect = undefined;
  const detach = () => {
    if (!openSelect && selectPopover.parentElement !== document.body) document.body.append(selectPopover);
  };
  clearTimeout(detachSelectTimer);
  if (motionEnabled()) detachSelectTimer = setTimeout(detach, 110);
  else queueMicrotask(detach);
}

function placeSelectPopover() {
  const { trigger, searchable } = openSelect;
  const rect = trigger.getBoundingClientRect();
  const below = innerHeight - rect.bottom - 12;
  const above = rect.top - 12;
  const desired = searchable ? 336 : Math.min(336, openSelect.select.length * 32 + 16);
  const opensAbove = below < desired && above > below;
  selectPopover.classList.toggle('opens-above', opensAbove);
  const width = Math.min(innerWidth - 16, Math.max(rect.width, searchable ? 220 : 150));
  selectPopover.style.width = width + 'px';
  const left = rect.left + width > innerWidth - 8 ? rect.right - width : rect.left;
  selectPopover.style.left = Math.max(8, Math.min(innerWidth - width - 8, left)) + 'px';
  selectPopover.style.top = opensAbove ? 'auto' : rect.bottom + 6 + 'px';
  selectPopover.style.bottom = opensAbove ? innerHeight - rect.top + 6 + 'px' : 'auto';
  selectPopover.style.maxHeight = Math.min(336, opensAbove ? above : below) + 'px';
}

function renderSelectOptions() {
  const query = selectSearch.value.trim().toLowerCase();
  const { select } = openSelect;
  const rows = [];
  const makeRow = option => {
    if (!option.label.toLowerCase().includes(query) && !option.value.toLowerCase().includes(query)) return null;
    const row = element('div', undefined, 'select-option');
    row.id = 'select-option-' + option.index;
    row.dataset.index = option.index;
    row.setAttribute('role', 'option');
    row.setAttribute('aria-selected', String(option.selected));
    const disabled = option.disabled || option.parentElement.disabled;
    if (disabled) row.setAttribute('aria-disabled', 'true');
    else rows.push(row);
    const check = element('span', option.selected ? '✓' : '', 'select-option-check');
    check.setAttribute('aria-hidden', 'true');
    row.append(element('span', option.label), element('span', option.dataset.date || '', 'select-option-date'), check);
    return row;
  };
  const content = [];
  for (const item of select.children) {
    if (item.tagName === 'OPTGROUP') {
      const options = [...item.children].map(makeRow).filter(Boolean);
      if (!options.length) continue;
      const group = element('div');
      group.setAttribute('role', 'group');
      group.setAttribute('aria-label', item.label);
      const heading = element('div', item.label, 'select-group-label');
      heading.setAttribute('aria-hidden', 'true');
      group.append(heading, ...options);
      content.push(group);
    } else if (item.tagName === 'OPTION') {
      const row = makeRow(item);
      if (row) content.push(row);
    }
  }
  selectResults.replaceChildren(...content);
  el('select-no-results').hidden = content.length > 0;
  openSelect.rows = rows;
  openSelect.active = -1;
  highlightSelectOption(rows.length ? Math.max(0, rows.findIndex(row => row.getAttribute('aria-selected') === 'true')) : -1);
}

function highlightSelectOption(index) {
  const { rows, active, focusTarget } = openSelect;
  rows[active]?.classList.remove('is-active');
  openSelect.active = index;
  const row = rows[index];
  if (!row) {
    focusTarget.removeAttribute('aria-activedescendant');
    return;
  }
  row.classList.add('is-active');
  focusTarget.setAttribute('aria-activedescendant', row.id);
  if (selectPopover.matches(':popover-open')) row.scrollIntoView({ block: 'nearest' });
}

function chooseSelectOption(row) {
  if (!row || row.getAttribute('aria-disabled') === 'true') return;
  const { select } = openSelect;
  const previous = select.selectedIndex;
  select.selectedIndex = Number(row.dataset.index);
  closeSelectPicker(true);
  updateSelectPicker(select);
  if (select.selectedIndex === previous) return;
  select.dispatchEvent(new Event('input', { bubbles: true }));
  select.dispatchEvent(new Event('change', { bubbles: true }));
}

if (typeof selectPopover.showPopover === 'function') {
  document.querySelectorAll('select').forEach(attachSelectPicker);
  new MutationObserver(records => {
    if (openSelect && !openSelect.trigger.isConnected) closeSelectPicker();
    for (const record of records) {
      const select = record.target.closest?.('select');
      if (select) updateSelectPicker(select);
      for (const node of record.addedNodes) {
        if (node instanceof HTMLSelectElement) attachSelectPicker(node);
        else node.querySelectorAll?.('select').forEach(attachSelectPicker);
      }
    }
  }).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['disabled', 'hidden', 'selected'] });

  selectPopover.addEventListener('beforetoggle', event => {
    if (event.newState === 'closed') clearSelectPicker();
  });
  selectPopover.addEventListener('focusout', event => {
    if (event.relatedTarget && !selectPopover.contains(event.relatedTarget) && event.relatedTarget !== openSelect?.trigger) closeSelectPicker();
  });
  selectSearch.addEventListener('input', event => {
    event.stopPropagation();
    if (openSelect) renderSelectOptions();
  });
  selectResults.addEventListener('mousedown', event => {
    if (event.target.closest('[role=option]')) event.preventDefault();
  });
  selectResults.addEventListener('click', event => {
    if (openSelect) chooseSelectOption(event.target.closest('[role=option]'));
  });
  selectPopover.addEventListener('keydown', event => {
    if (!openSelect || event.isComposing) return;
    const { rows, active, searchable } = openSelect;
    const key = event.key;
    if (!['Escape', 'Enter', 'ArrowDown', 'ArrowUp'].includes(key) &&
        (searchable || !['Home', 'End', ' '].includes(key))) return;
    event.preventDefault();
    event.stopPropagation();
    if (key === 'Escape') closeSelectPicker(true);
    else if (key === 'Enter' || key === ' ') chooseSelectOption(rows[active]);
    else if (rows.length) {
      const index = key === 'Home' ? 0 : key === 'End' ? rows.length - 1 : active + (key === 'ArrowDown' ? 1 : -1);
      highlightSelectOption(Math.min(rows.length - 1, Math.max(0, index)));
    }
  });
  window.addEventListener('resize', () => { if (openSelect) placeSelectPopover(); });
  document.addEventListener('scroll', event => {
    if (openSelect && !selectPopover.contains(event.target)) closeSelectPicker();
  }, true);
}
