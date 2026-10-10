(() => {
  'use strict';
  const root = document.querySelector('.store-assistant');
  if (!root) return;
  const panel = root.querySelector('.assistant-panel'), toggle = root.querySelector('.assistant-toggle');
  const form = root.querySelector('form'), input = root.querySelector('textarea');
  const messages = root.querySelector('.assistant-messages'), status = root.querySelector('.assistant-status');
  const send = form.querySelector('button[type="submit"]');
  const footer = document.querySelector('.site-footer');
  if (footer) {
    const dockAboveFooter = () => {
      const overlap = Math.max(0, Math.min(window.innerHeight, window.innerHeight - footer.getBoundingClientRect().top));
      root.style.setProperty('--footer-overlap', `${overlap}px`);
    };
    window.addEventListener('scroll', dockAboveFooter, {passive:true});
    window.addEventListener('resize', dockAboveFooter);
    dockAboveFooter();
  }
  let pending = null, busy = false;
  function open(value) { panel.hidden = !value; toggle.setAttribute('aria-expanded', String(value)); if (value) input.focus(); }
  toggle.addEventListener('click', () => open(panel.hidden));
  root.querySelector('.assistant-close').addEventListener('click', () => { open(false); toggle.focus(); });
  root.addEventListener('keydown', e => { if (e.key === 'Escape') { open(false); toggle.focus(); } });
  function bubble(text, user = false) {
    const p = document.createElement('p'); p.className = 'assistant-bubble' + (user ? ' user' : '');
    p.textContent = text; messages.append(p); messages.scrollTop = messages.scrollHeight;
  }
  function cards(products) {
    for (const p of products) {
      const article = document.createElement('article'); article.className = 'assistant-product';
      if (p.imageUrl) { const image = document.createElement('img'); image.src = p.imageUrl; image.alt = p.name; image.loading = 'lazy'; image.referrerPolicy = 'no-referrer'; article.append(image); }
      const info = document.createElement('div'), link = document.createElement('a');
      link.href = p.productUrl; link.textContent = p.name;
      const price = document.createElement('p'); price.textContent = `${p.unitPrice} د.أ · ${p.available ? 'متوفر' : 'غير متوفر حالياً'}`;
      info.append(link, price); article.append(info); messages.append(article);
    }
    messages.scrollTop = messages.scrollHeight;
  }
  form.addEventListener('submit', async e => {
    e.preventDefault(); if (busy) return;
    const text = input.value.trim(); if (!text || text.length > 2000) return;
    // An uncertain request keeps its ID and original text; retrying cannot create a second AI turn.
    if (pending && pending.text !== text) { status.textContent = 'أعد إرسال الرسالة السابقة أولاً، أو حدّث الصفحة لبدء جلسة عرض جديدة.'; return; }
    if (!pending) { pending = {text, requestId: crypto.randomUUID()}; bubble(text, true); }
    busy = true; send.disabled = true; input.readOnly = true; status.textContent = 'عم أتحقق من المعلومات…';
    try {
      const response = await fetch(root.dataset.assistantEndpoint, {method:'POST', credentials:'same-origin',
        headers:{'Content-Type':'application/json','RequestVerificationToken':form.querySelector('[name="__RequestVerificationToken"]').value}, body:JSON.stringify(pending)});
      const body = await response.json();
      if (!response.ok || body.success !== true) {
        status.textContent = body.message || 'تعذر الرد. جرّب نفس الرسالة بعد شوي.';
        if (body.error === 'catalog_changed') { pending = null; send.textContent = 'تحقق من جديد'; }
        else send.textContent = 'إعادة المحاولة';
        return;
      }
      if (body.requestId !== pending.requestId || typeof body.reply !== 'string' || !Array.isArray(body.products)) throw new Error('invalid_response');
      bubble(body.reply); cards(body.products); pending = null; input.value = ''; status.textContent = ''; send.textContent = 'إرسال';
      if (body.hasMore) {
        const more = document.createElement('button'); more.type = 'button'; more.className = 'assistant-more'; more.textContent = 'اعرض باقي الأنواع';
        more.addEventListener('click', () => { if (!busy && !pending) { more.disabled = true; input.value = 'كمل، اعرض باقي الأنواع'; form.requestSubmit(); } }); messages.append(more);
      }
    } catch { status.textContent = 'انقطع الاتصال. اضغط إعادة المحاولة لنفس الرسالة.'; send.textContent = 'إعادة المحاولة'; }
    finally { busy = false; send.disabled = false; input.readOnly = false; }
  });
})();
