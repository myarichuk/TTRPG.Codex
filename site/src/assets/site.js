/* TTRPG.Codex website: a few progressive enhancements. Every page works without this file. */
(() => {
    'use strict';
    const $ = (sel, root = document) => root.querySelector(sel);
    const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
    const store = {
        get(key) { try { return localStorage.getItem(key); } catch { return null; } },
        set(key, value) { try { localStorage.setItem(key, value); } catch { /* private mode: not worth failing for */ } },
    };

    // ---- theme -------------------------------------------------------------------------------
    const root = document.documentElement;
    $$('[data-theme-toggle]').forEach((btn) => btn.addEventListener('click', () => {
        const next = root.getAttribute('data-theme') === 'light' ? 'dark' : 'light';
        root.setAttribute('data-theme', next);
        store.set('codex-site-theme', next);
        btn.setAttribute('aria-pressed', String(next === 'light'));
    }));

    // ---- mobile menu -------------------------------------------------------------------------
    const header = $('.site-header');
    const menuBtn = $('[data-menu-toggle]');
    if (header && menuBtn) {
        menuBtn.addEventListener('click', () => {
            const open = header.toggleAttribute('data-open');
            menuBtn.setAttribute('aria-expanded', String(open));
        });
        $$('.nav a').forEach((a) => a.addEventListener('click', () => { header.removeAttribute('data-open'); menuBtn.setAttribute('aria-expanded', 'false'); }));
    }

    // ---- copy buttons ------------------------------------------------------------------------
    $$('[data-copy]').forEach((btn) => btn.addEventListener('click', async () => {
        const code = btn.closest('.codeblock')?.querySelector('code');
        if (!code) return;
        try {
            await navigator.clipboard.writeText(code.textContent.replace(/\n$/, ''));
            const label = btn.querySelector('[data-label]');
            const original = label.textContent;
            label.textContent = btn.dataset.copied;
            btn.setAttribute('data-done', '');
            setTimeout(() => { label.textContent = original; btn.removeAttribute('data-done'); }, 1800);
        } catch { /* clipboard blocked: the text stays selectable */ }
    }));

    // ---- lightbox ----------------------------------------------------------------------------
    const box = $('dialog.lightbox');
    if (box && typeof box.showModal === 'function') {
        const img = $('img', box), cap = $('.cap', box);
        $$('button.zoom').forEach((btn) => btn.addEventListener('click', () => {
            const src = $('img', btn);
            img.src = btn.dataset.full || src.currentSrc || src.src;
            img.alt = src.alt;
            cap.textContent = btn.dataset.caption || src.alt;
            box.showModal();
        }));
        box.addEventListener('click', (e) => { if (e.target === box || e.target.closest('[data-close]')) box.close(); });
    } else {
        $$('button.zoom').forEach((btn) => { btn.style.cursor = 'default'; });
    }

    // ---- table of contents: mark the section being read --------------------------------------
    const tocLinks = $$('.toc a');
    if (tocLinks.length && 'IntersectionObserver' in window) {
        const byId = new Map(tocLinks.map((a) => [decodeURIComponent(a.hash.slice(1)), a]));
        const visible = new Set();
        const mark = () => {
            const first = [...byId.keys()].find((id) => visible.has(id)) ?? [...byId.keys()].filter((id) => document.getElementById(id).getBoundingClientRect().top < 120).pop();
            tocLinks.forEach((a) => a.toggleAttribute('aria-current', a === byId.get(first)));
            if (first) byId.get(first).setAttribute('aria-current', 'true');
        };
        const io = new IntersectionObserver((entries) => {
            entries.forEach((e) => (e.isIntersecting ? visible.add(e.target.id) : visible.delete(e.target.id)));
            mark();
        }, { rootMargin: '-80px 0px -65% 0px' });
        byId.forEach((_, id) => { const el = document.getElementById(id); if (el) io.observe(el); });
    }

    // ---- suggest the visitor's language, once, without redirecting ---------------------------
    $$('[data-lang-link]').forEach((a) => a.addEventListener('click', () => store.set('codex-site-lang', 'chosen')));
    const banner = $$('.lang-banner').find((b) => (navigator.languages || [navigator.language]).some((l) => (l || '').toLowerCase().startsWith(b.dataset.for)));
    if (banner && !store.get('codex-site-lang')) {
        banner.hidden = false;
        $('button', banner).addEventListener('click', () => { banner.hidden = true; store.set('codex-site-lang', 'chosen'); });
    }

    // ---- downloads: highlight the visitor's system, link the newest release's own files ------
    const ua = navigator.userAgent || '';
    const os = /Android|iPhone|iPad/.test(ua) ? null : /Windows/.test(ua) ? 'win' : /Mac/.test(ua) ? 'mac' : /Linux|X11|CrOS/.test(ua) ? 'linux' : null;
    $$('.platform[data-os]').forEach((card) => {
        const mine = card.dataset.os === os;
        card.toggleAttribute('data-recommended', mine);
        const pill = $('.pill', card);
        if (pill) pill.hidden = !mine;
    });

    const pick = {
        'win-setup': (n) => /-setup\.exe$/i.test(n),
        'win-zip': (n) => /\.zip$/i.test(n),
        'mac-pkg': (n) => /\.pkg$/i.test(n),
        'mac-tgz': (n) => /\.tar\.gz$/i.test(n) && /osx|macos|mac/i.test(n),
        'linux-deb': (n) => /\.deb$/i.test(n),
        'linux-tgz': (n) => /\.tar\.gz$/i.test(n) && /linux/i.test(n),
    };
    const hosts = $$('[data-release]');
    if (hosts.length && 'fetch' in window) {
        fetch(hosts[0].dataset.release, { headers: { Accept: 'application/vnd.github+json' } })
            .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
            .then((release) => {
                $$('[data-release-version]').forEach((el) => { el.textContent = release.tag_name; el.closest('[hidden]')?.removeAttribute('hidden'); });
                $$('[data-release-date]').forEach((el) => {
                    el.textContent = new Intl.DateTimeFormat(document.documentElement.lang, { dateStyle: 'long' }).format(new Date(release.published_at));
                });
                $$('[data-asset]').forEach((a) => {
                    const asset = release.assets.find((x) => pick[a.dataset.asset]?.(x.name));
                    if (asset) { a.href = asset.browser_download_url; a.title = asset.name; }
                });
            })
            .catch(() => { /* rate-limited or offline: the links already point at the releases page */ });
    }
})();
