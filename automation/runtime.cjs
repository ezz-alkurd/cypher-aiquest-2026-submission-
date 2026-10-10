'use strict';

// Pure domain helpers. Network and SQL operations belong to their n8n nodes.
const fail = (code) => { throw new Error(code); };
const insist = (condition, code) => { if (!condition) fail(code); };
const text = (value, max = 1000) => typeof value === 'string' ? value.trim().slice(0, max) : '';
const integer = (value, min = 0, max = 1000000000) => {
  insist(Number.isSafeInteger(Number(value)) && Number(value) >= min && Number(value) <= max, 'invalid_integer');
  return Number(value);
};
const arabic = value => text(value).normalize('NFKC').replace(/[\u064b-\u065f\u0670\u0640]/g, '').replace(/[أإآ]/g, 'ا').replace(/ى/g, 'ي').replace(/ة/g, 'ه').toLowerCase().replace(/\s+/g, ' ');
const confirmation = value => arabic(value) === 'تاكيد';
const money = cents => (integer(cents) / 100).toFixed(2);

function normalizeWebhook(webhook) {
  const body = webhook.body || {};
  const event = text(body.event, 50).toLowerCase().replace(/_/g, '.');
  if (event !== 'messages.upsert') return {ignored: true, reason: 'not_message'};
  const data = body.data || {};
  if (Array.isArray(data)) return {ignored: true, reason: 'batch_not_supported'};
  const key = data.key || {};
  if (key.fromMe === true || key.fromMe === 'true') return {ignored: true, reason: 'from_me'};
  const rawJid = text(key.remoteJid, 150);
  if (/@g\.us$|@broadcast$|@newsletter$/.test(rawJid)) return {ignored: true, reason: 'not_private'};
  const candidates = [key.remoteJidAlt, data.senderPn, key.senderPn, key.remoteJid];
  const jid = candidates.find(v => typeof v === 'string' && /^[1-9][0-9]{7,14}(?::\d+)?@s\.whatsapp\.net$/.test(v));
  // LID is never guessed to be a phone number.
  if (!jid) return {ignored: true, reason: 'unresolved_phone_identity'};
  const recipient = jid.split('@')[0].split(':')[0];
  const instance = text(body.instance, 100);
  const messageId = text(key.id, 200);
  insist(/^[A-Za-z0-9_-]{1,100}$/.test(instance) && messageId, 'missing_routing_identity');
  let message = data.message || {};
  for (let i = 0; i < 5; i++) {
    const inner = message.ephemeralMessage?.message || message.viewOnceMessage?.message || message.viewOnceMessageV2?.message || message.documentWithCaptionMessage?.message;
    if (!inner) break;
    message = inner;
  }
  const messageText = text(message.conversation || message.extendedTextMessage?.text || message.imageMessage?.caption || message.documentMessage?.caption, 6000);
  return {ignored: false, instanceName: instance, messageId, recipient, remoteJid: jid, text: messageText,
    customerNameHint: text(data.pushName, 150), channel: 'whatsapp', audio: Boolean(message.audioMessage),
    mediaMessage: message.audioMessage ? data : null, supported: Boolean(messageText || message.audioMessage)};
}

function payload(rows) {
  insist(Array.isArray(rows) && rows.length === 1, 'sql_contract_requires_one_payload_row');
  const raw = rows[0].payload;
  const result = typeof raw === 'string' ? JSON.parse(raw) : raw;
  insist(result && typeof result === 'object' && !Array.isArray(result), 'sql_payload_missing');
  return result;
}

function verifyScope(result, input) {
  insist(result.storeId === input.storeId, 'store_scope_mismatch');
  if (input.sessionId) insist(result.sessionId === input.sessionId, 'session_scope_mismatch');
  return result;
}

function verifyRouted(result, inbound) {
  if (result.ignored === true) return {ignored: true, reason: text(result.reason, 80)};
  insist(result.ok === true, 'route_not_ready');
  insist(Number.isSafeInteger(result.storeId) && result.storeId > 0, 'invalid_store_scope');
  insist(result.instanceName === inbound.instanceName && result.recipient === inbound.recipient, 'instance_recipient_mismatch');
  insist(result.whatsappReady === true && result.storeActive === true, 'store_whatsapp_not_ready');
  insist(text(result.sessionId, 200) && text(result.conversationId, 200) && text(result.leaseToken, 200), 'session_lease_missing');
  insist(result.messageId === inbound.messageId, 'message_scope_mismatch');
  return {...result, text: inbound.text, supported: inbound.supported, audio: inbound.audio,
    mediaMessage: inbound.mediaMessage, customerNameHint: inbound.customerNameHint, channel: 'whatsapp', ignored: false};
}

function inventory(input, result) {
  verifyScope(result, input);
  insist(Array.isArray(result.products), 'inventory_products_missing');
  const products = result.products.filter(p => p.isActive === true).map(p => {
    insist(p.storeId === input.storeId, 'cross_store_product');
    return {...p, productId: integer(p.productId, 1), priceCents: integer(p.priceCents), stockQuantity: integer(p.stockQuantity)};
  });
  if (input.mode === 'categories') return {success: true, status: 'categories', categories: [...new Set(products.map(p => p.category))].filter(Boolean)};
  insist(input.mode === 'search', 'invalid_inventory_mode');
  const query = arabic(input.query);
  insist(query, 'empty_product_query');
  const fields = p => [p.name, p.sku, ...(Array.isArray(p.aliases) ? p.aliases : String(p.aliases || '').split(','))];
  const exact = products.filter(p => fields(p).some(v => arabic(v) === query));
  const found = exact.length ? exact : products.filter(p => fields(p).concat(p.description).some(v => arabic(v).includes(query)));
  const requested = integer(input.requestedQuantity || 1, 1, 100000);
  const safe = p => ({productId: p.productId, name: p.name, category: p.category, description: p.description,
    unit: p.unit, unitPrice: money(p.priceCents), priceCents: p.priceCents, available: p.stockQuantity > 0,
    canFulfillRequestedQuantity: p.stockQuantity >= requested});
  return {success: true, status: found.length === 0 ? 'not_found' : found.length === 1 ? 'unique' : 'ambiguous',
    ...(found.length === 1 ? {product: safe(found[0])} : {options: found.slice(0, 8).map(safe)})};
}

function cartSummary(cart) {
  const items = cart.items.map((p, index) => ({itemIndex: index + 1, productId: p.productId, name: p.name,
    quantity: p.quantity, priceCents: p.priceCents, lineTotalCents: p.quantity * p.priceCents,
    unitPrice: money(p.priceCents), lineTotal: money(p.quantity * p.priceCents)}));
  const subtotalCents = items.reduce((sum, item) => sum + item.lineTotalCents, 0);
  const deliveryFeeCents = items.length ? integer(cart.deliveryFeeCents) : 0;
  const totalCents = subtotalCents + deliveryFeeCents;
  integer(totalCents);
  const missingFields = [];
  if (!text(cart.customerName, 150)) missingFields.push('customerName');
  if (!text(cart.deliveryAddress, 500)) missingFields.push('deliveryAddress');
  return {...cart, items, subtotalCents, totalCents, deliveryFeeCents, subtotal: money(subtotalCents),
    deliveryFee: money(deliveryFeeCents), total: money(totalCents), currency: 'JOD', missingFields};
}

function manageCart(input, result) {
  verifyScope(result, input);
  insist(result.leaseToken === input.leaseToken, 'lease_scope_mismatch');
  insist(result.conversationId === input.conversationId, 'conversation_scope_mismatch');
  insist(result.cart && Array.isArray(result.cart.items) && Array.isArray(result.products), 'cart_snapshot_missing');
  const cart = JSON.parse(JSON.stringify(result.cart));
  insist(cart.status !== 'submitted', 'submitted_cart_requires_new_conversation');
  const actions = ['get', 'add', 'set_quantity', 'increment', 'decrement', 'remove', 'clear', 'update_customer'];
  insist(actions.includes(input.action), 'invalid_cart_action');
  const products = new Map(result.products.map(p => {
    insist(p.storeId === input.storeId, 'cross_store_product');
    return [integer(p.productId, 1), p];
  }));
  cart.items.forEach(item => {
    integer(item.productId, 1); integer(item.quantity, 1, 100000); integer(item.priceCents);
  });
  const productId = Number(input.productId || 0);
  const itemIndex = integer(input.itemIndex || 0, 0, 1000);
  const target = itemIndex > 0 ? cart.items[itemIndex - 1] : cart.items.find(p => p.productId === productId);
  const quantityActions = ['add', 'set_quantity', 'increment', 'decrement'];
  const quantity = quantityActions.includes(input.action) ? integer(input.quantity, 1, 100000) : 0;
  if (input.action === 'clear') cart.items = [];
  if (input.action === 'update_customer') {
    if (text(input.customerName, 150)) cart.customerName = text(input.customerName, 150);
    if (text(input.deliveryAddress, 500)) cart.deliveryAddress = text(input.deliveryAddress, 500);
    if (typeof input.customerNote === 'string' && input.customerNote !== '__UNCHANGED__') cart.customerNote = text(input.customerNote, 1000);
  }
  if (input.action === 'remove') {
    insist(target, 'cart_item_not_found');
    cart.items = cart.items.filter(p => p.productId !== target.productId);
  }
  if (quantityActions.includes(input.action)) {
    const id = input.action === 'add' ? integer(productId, 1) : target?.productId;
    insist(id, 'cart_item_not_found');
    const product = products.get(id);
    insist(product && product.isActive === true, 'product_not_available');
    const current = cart.items.find(p => p.productId === id);
    let next = quantity;
    if (input.action === 'add' || input.action === 'increment') next = (current?.quantity || 0) + quantity;
    if (input.action === 'decrement') next = (current?.quantity || 0) - quantity;
    insist(next <= integer(product.stockQuantity) && next <= 100000, 'insufficient_stock');
    const position = cart.items.findIndex(p => p.productId === id);
    if (next <= 0) cart.items = cart.items.filter(p => p.productId !== id);
    else {
      const line = {productId: id, name: text(product.name, 200), quantity: next, priceCents: integer(product.priceCents)};
      if (position >= 0) cart.items[position] = line;
      else cart.items.push(line);
    }
  }
  // Refresh every remaining line against current catalog prices and availability.
  for (const item of cart.items) {
    const p = products.get(item.productId);
    insist(p && p.isActive === true, 'product_not_available');
    insist(item.quantity <= integer(p.stockQuantity), 'insufficient_stock');
    item.priceCents = integer(p.priceCents); item.name = text(p.name, 200);
  }
  cart.deliveryFeeCents = integer(result.deliveryFeeCents);
  cart.status = cart.items.length ? 'building_order' : 'idle';
  const expectedVersion = integer(result.cart.version);
  const summary = cartSummary(cart);
  return {storeId: input.storeId, sessionId: input.sessionId, conversationId: input.conversationId,
    leaseToken: input.leaseToken, messageId: input.messageId, operationId: input.operationId, action: input.action, expectedVersion,
    // SQL must persist this CAS result before success can be returned to the customer.
    candidate: {...summary, version: expectedVersion + 1}};
}

function committedCart(input, result) {
  verifyScope(result, input);
  insist(result.success === true && result.cart && result.cart.version === input.candidate.version, 'cart_commit_not_acknowledged');
  insist(result.conversationId === input.conversationId && result.leaseToken === input.leaseToken, 'cart_commit_scope_mismatch');
  const cart = cartSummary(result.cart);
  const cartText = cart.items.length ? 'طلبك الحالي:\n' + cart.items.map(p => `${p.itemIndex}. ${p.quantity} × ${p.name} — ${p.lineTotal} د.أ`).join('\n') + `\nالتوصيل: ${cart.deliveryFee} د.أ\nالمجموع: ${cart.total} د.أ` : 'السلة فاضية حالياً.';
  return {success: true, cart, cartText, missingFields: cart.missingFields};
}

function prepareConfirmation(input, result) {
  insist(confirmation(input.confirmationText), 'explicit_confirmation_required');
  verifyScope(result, input);
  insist(result.leaseToken === input.leaseToken && result.conversationId === input.conversationId, 'confirmation_scope_mismatch');
  if (result.existingOrder) return {alreadySubmitted: true, existingOrder: result.existingOrder, ...input};
  const cart = cartSummary(result.cart);
  insist(cart.items.length && cart.missingFields.length === 0, 'checkout_fields_missing');
  // Only the latest successfully delivered summary is confirmable.
  insist(result.lastShownVersion === cart.version && result.lastShownDeliveryStatus === 'sent', 'latest_summary_not_delivered');
  const products = new Map(result.products.map(p => [p.productId, p]));
  for (const item of cart.items) {
    const p = products.get(item.productId);
    insist(p?.storeId === input.storeId && p.isActive === true, 'product_not_available');
    insist(item.quantity <= integer(p.stockQuantity), 'insufficient_stock');
    insist(item.priceCents === integer(p.priceCents), 'price_changed_requires_new_summary');
  }
  insist(cart.deliveryFeeCents === result.deliveryFeeCents, 'delivery_changed_requires_new_summary');
  return {...input, cart, expectedVersion: cart.version, externalOrderId: `wa:${input.conversationId}:v${cart.version}`,
    sourceChannel: 'whatsapp', status: 'NeedsReview', stockReserved: false, alreadySubmitted: false};
}

function confirmedOrder(input, result) {
  verifyScope(result, input);
  insist(result.success === true && result.order?.status === 'NeedsReview' && result.order.orderNumber, 'order_not_committed');
  insist(result.conversationId === input.conversationId, 'order_conversation_mismatch');
  return {success: true, orderId: result.order.id, orderNumber: result.order.orderNumber,
    reply: `وصل طلبك للمتجر برقم ${text(result.order.orderNumber, 24)} ✅\nالمجموع: ${money(result.order.totalCents)} د.أ. الطلب بانتظار مراجعة صاحب المتجر وتأكيد التوصيل.`, cartVersion: null};
}

function verifySendClaim(input, result) {
  verifyScope(result, input);
  insist(result.leaseToken === input.leaseToken && result.messageId === input.messageId, 'send_lease_mismatch');
  insist(result.instanceName === input.instanceName && result.recipient === input.recipient, 'send_target_mismatch');
  if (result.status === 'sent') return {...input, claimed: false, alreadySent: true};
  insist(result.status === 'claimed' && result.claimId, 'send_claim_not_acquired');
  insist(text(input.reply, 4000), 'empty_reply');
  return {...input, claimed: true, alreadySent: false, claimId: result.claimId};
}

function onboardingInput(body) {
  const storeId = integer(body.storeId, 1);
  const ownerUserId = text(body.ownerUserId, 200);
  insist(ownerUserId && ['connect', 'verify'].includes(body.action), 'invalid_onboarding_request');
  // The website backend supplies the authenticated owner identity; a browser must not call this directly.
  return {storeId, ownerUserId, action: body.action, expectedPhone: text(body.expectedPhone, 20)};
}

function verifyOnboarding(input, result) {
  insist(result.authorized === true && result.storeId === input.storeId && result.ownerUserId === input.ownerUserId, 'owner_store_not_authorized');
  insist(/^[1-9][0-9]{7,14}$/.test(result.expectedPhone), 'expected_phone_missing');
  insist(/^cypher-[0-9]+$/.test(result.instanceName) && result.instanceName === `cypher-${input.storeId}`, 'onboarding_instance_mismatch');
  insist(result.onboardingToken, 'onboarding_lease_missing');
  return {...result, action: input.action};
}

function connectedOwner(context, response) {
  const records = Array.isArray(response) ? response : [response];
  const record = records.find(p => (p.name || p.instanceName || p.instance?.instanceName) === context.instanceName);
  const jid = record?.ownerJid || record?.owner || record?.instance?.owner || '';
  const digits = String(jid).split('@')[0].split(':')[0];
  insist(digits === context.expectedPhone && /^[1-9][0-9]{7,14}$/.test(digits), 'connected_owner_phone_mismatch');
  return {...context, ownerVerified: true};
}

module.exports = {normalizeWebhook, payload, verifyScope, verifyRouted, inventory, manageCart, cartSummary, committedCart,
  confirmation, prepareConfirmation, confirmedOrder, verifySendClaim, onboardingInput, verifyOnboarding, connectedOwner, arabic};
