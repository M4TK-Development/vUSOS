export function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c] ?? c);
}

const PAGE_STYLE = `body{font-family:system-ui,sans-serif;max-width:26rem;margin:4rem auto;padding:0 1rem;color:#1a1a1a}
label{display:block;margin:.75rem 0 .25rem;font-size:.9rem}input{width:100%;padding:.5rem;box-sizing:border-box;font-size:1rem}
button{margin-top:1rem;padding:.6rem 1rem;font-size:1rem;cursor:pointer}.note{color:#666;font-size:.85rem}`;

export function page(title: string, body: string): string {
  return `<!doctype html><html lang="pl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${escapeHtml(title)}</title><style>${PAGE_STYLE}</style></head><body>${body}</body></html>`;
}

export function devLoginPage(client: "web" | "native" = "web"): string {
  return page(
    "vUSOS — logowanie (dev)",
    `<h1>vUSOS</h1><p class="note">Tryb deweloperski (AUTH_MODE=dev): logowanie bez Microsoft Entra ID.</p>
<form method="post" action="/api/auth/login">
<input type="hidden" name="client" value="${client}">
<label for="email">E-mail</label><input id="email" name="email" type="email" required autofocus placeholder="student@stu.vistula.edu.pl">
<label for="name">Imię i nazwisko</label><input id="name" name="name" type="text" required maxlength="100" placeholder="Jan Kowalski">
<button type="submit">Zaloguj</button></form>`
  );
}

export function forbiddenPage(message: string): string {
  return page("vUSOS — brak dostępu", `<h1>Brak dostępu</h1><p>${escapeHtml(message)}</p><p><a href="/api/auth/login">Spróbuj ponownie</a></p>`);
}
