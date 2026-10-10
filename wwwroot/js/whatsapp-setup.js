(() => {
  'use strict'; const root = document.querySelector('#whatsapp-setup'); if (!root) return;
  const buttons = [...root.querySelectorAll('[data-setup-action]')], status = root.querySelector('[role="status"]'), image = root.querySelector('img');
  for (const button of buttons) button.addEventListener('click', async () => {
    buttons.forEach(b => b.disabled = true); status.textContent = 'عم أتحقق من الاتصال…';
    try {
      const response = await fetch('/admin/whatsapp/setup', {method:'POST',credentials:'same-origin',
        headers:{'Content-Type':'application/json',RequestVerificationToken:root.querySelector('[name="__RequestVerificationToken"]').value},
        body:JSON.stringify({action:button.dataset.setupAction})});
      const body = await response.json(); status.textContent = body.message || 'تعذر ربط واتساب.';
      if (response.ok && body.success === true && /^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(body.qrBase64 || '')) { image.src = body.qrBase64; image.hidden = false; }
      else { image.hidden = true; image.removeAttribute('src'); }
    } catch { status.textContent = 'تعذر الوصول. انتظر شوي وجرّب لاحقاً.'; }
    finally { buttons.forEach(b => b.disabled = false); }
  });
})();
