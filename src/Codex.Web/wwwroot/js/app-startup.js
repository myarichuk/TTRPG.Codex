window.codexTimeZone = function () { return Intl.DateTimeFormat().resolvedOptions().timeZone; };

// The interface language the server chose (the account's, from its cookie, which scripts can't read), kept for offline.html,
// which the service worker shows with no server to ask (U7).
try { localStorage.setItem("codex-lang", document.documentElement.lang || "en"); } catch (e) { /* storage blocked: the browser's language */ }

(function () {
    // ---- Splash ----
    function removeSplash() {
        const el = document.getElementById("boot-splash");
        if (!el) return;
        el.style.opacity = '0';
        el.style.transition = 'opacity 0.3s ease';
        setTimeout(function () {
            if (el.parentNode) el.parentNode.removeChild(el);
        }, 300);
    }

    // ---- Reconnect modal ----
    function initReconnectLogic() {
        const reconnectModal = document.getElementById("components-reconnect-modal");
        const retryButton = document.getElementById("components-reconnect-button");
        const resumeButton = document.getElementById("components-resume-button");
        if (!reconnectModal) return;

        reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);
        if (retryButton) retryButton.addEventListener("click", retry);
        if (resumeButton) resumeButton.addEventListener("click", resume);

        function handleReconnectStateChanged(event) {
            if (event.detail.state === "show") {
                if (reconnectModal.showModal && !reconnectModal.open) reconnectModal.showModal();
            } else if (event.detail.state === "hide") {
                if (reconnectModal.close && reconnectModal.open) reconnectModal.close();
            } else if (event.detail.state === "failed") {
                document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
            } else if (event.detail.state === "rejected") {
                mustReload();
            }
        }

        // The server no longer has this page's session, so it has to reload. Said, not done behind the person's back
        // (D9): what they typed is kept (see Drafts) and offered back once the page is up again.
        function mustReload() {
            Array.from(reconnectModal.classList)
                .filter(function (c) { return c.indexOf("components-reconnect-") === 0 || c === "components-reconnect-paused"; })
                .forEach(function (c) { reconnectModal.classList.remove(c); });
            reconnectModal.classList.add("components-reconnect-rejected");
            if (reconnectModal.showModal && !reconnectModal.open) reconnectModal.showModal();
        }

        async function retry() {
            document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
            try {
                const successful = await Blazor.reconnect();
                if (!successful) {
                    const resumeSuccessful = await Blazor.resumeCircuit();
                    if (!resumeSuccessful) {
                        mustReload();
                    } else if (reconnectModal.close) {
                        reconnectModal.close();
                    }
                }
            } catch (err) {
                document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
            }
        }

        async function resume() {
            try {
                const successful = await Blazor.resumeCircuit();
                if (!successful) mustReload();
            } catch {
                mustReload();
            }
        }

        async function retryWhenDocumentBecomesVisible() {
            if (document.visibilityState === "visible") await retry();
        }
    }

    // ---- Drafts (D9) ----
    // What someone types into a text box is copied to this tab's sessionStorage, per page and box, as they type. When the
    // page has to reload because the server lost its session (a phone locked for a few minutes mid-entry), the text is
    // offered back once the page is up again, for the boxes whose text differs from what was saved. Only after such a
    // reload: ordinary navigation offers nothing, and the copies are dropped once offered or after a day.
    var DRAFT_PREFIX = "codex.draft:";
    var RECOVERING = "codex.recovering";
    var DRAFT_TTL = 24 * 60 * 60 * 1000;

    function draftable(el) {
        if (!el || !el.id || el.readOnly || el.disabled || el.hasAttribute("data-no-draft")) return false;
        if (el.tagName === "TEXTAREA") return true;
        return el.tagName === "INPUT" && (el.type === "text" || el.type === "search" || el.type === "url" || el.type === "number");
    }

    function draftKey(path, id) { return DRAFT_PREFIX + path + "#" + id; }

    function storage(fn) { try { return fn(window.sessionStorage); } catch (e) { return null; } }

    function keepDrafts() {
        document.addEventListener("input", function (e) {
            var el = e.target;
            if (!draftable(el)) return;
            storage(function (s) { s.setItem(draftKey(location.pathname, el.id), JSON.stringify({ v: el.value, t: Date.now() })); });
        }, true);

        document.addEventListener("click", function (e) {
            var button = e.target && e.target.closest ? e.target.closest("[data-codex-reload]") : null;
            if (!button) return;
            storage(function (s) { s.setItem(RECOVERING, location.pathname); });
            location.reload();
        });
    }

    function draftsFor(path) {
        return storage(function (s) {
            var found = [];
            for (var i = s.length - 1; i >= 0; i--) {
                var key = s.key(i);
                if (!key || key.indexOf(DRAFT_PREFIX) !== 0) continue;
                var draft = null;
                try { draft = JSON.parse(s.getItem(key)); } catch (e) { }
                if (!draft || Date.now() - draft.t > DRAFT_TTL) { s.removeItem(key); continue; }
                var prefix = DRAFT_PREFIX + path + "#";
                if (key.indexOf(prefix) === 0) found.push({ key: key, id: key.substring(prefix.length), value: draft.v });
            }
            return found;
        }) || [];
    }

    function forget(drafts) {
        storage(function (s) {
            drafts.forEach(function (d) { s.removeItem(d.key); });
            s.removeItem(RECOVERING);
        });
    }

    // After a reload the page loads its data on its own time, so the boxes are looked for for a little while.
    function offerDrafts() {
        var path = storage(function (s) { return s.getItem(RECOVERING); });
        var drafts = draftsFor(location.pathname);
        if (path !== location.pathname || drafts.length === 0) {
            if (path) storage(function (s) { s.removeItem(RECOVERING); });
            return;
        }

        var tries = 0;
        (function look() {
            var waiting = drafts.filter(function (d) {
                var el = document.getElementById(d.id);
                return draftable(el) && el.value !== d.value;
            });
            if (waiting.length === 0 && ++tries < 20) { setTimeout(look, 500); return; }
            if (waiting.length === 0) { forget(drafts); return; }
            show(waiting);
        })();

        function show(waiting) {
            var banner = document.getElementById("codex-draft-recovery");
            if (!banner) { forget(drafts); return; }
            var text = waiting.length === 1 ? banner.getAttribute("data-one") : banner.getAttribute("data-many").replace("{0}", waiting.length);
            document.getElementById("codex-draft-recovery-text").textContent = text;
            banner.hidden = false;
            document.getElementById("codex-draft-restore").onclick = function () {
                // Looked up again: the page may have finished loading since the banner appeared.
                waiting.forEach(function (d) {
                    var el = document.getElementById(d.id);
                    if (!draftable(el) || el.value === d.value) return;
                    el.value = d.value;
                    // Both, so a box bound on change and one bound as you type each take the text into the page.
                    el.dispatchEvent(new Event("input", { bubbles: true }));
                    el.dispatchEvent(new Event("change", { bubbles: true }));
                });
                banner.hidden = true;
                forget(drafts);
            };
            document.getElementById("codex-draft-discard").onclick = function () {
                banner.hidden = true;
                forget(drafts);
            };
        }
    }

    // ---- Installable app ----
    function registerServiceWorker() {
        if (!('serviceWorker' in navigator)) return;
        // Only on secure origins (https or localhost); registration silently fails elsewhere.
        navigator.serviceWorker.register('/sw.js', { scope: '/' }).catch(function (err) {
            console.debug('Service worker not registered:', err && err.message);
        });
    }

    function init() {
        registerServiceWorker();
        initReconnectLogic();
        keepDrafts();
        if (window.Blazor) {
            try {
                Blazor.start().then(function () { removeSplash(); setTimeout(offerDrafts, 1500); }).catch(removeSplash);
            } catch (e) {
                removeSplash();
            }
        } else {
            setTimeout(removeSplash, 2000);
        }

        window.addEventListener("load", function () {
            setTimeout(removeSplash, 8000);
        });
    }

    // Quick-entry forms (the table's notes drawer): the box empties the moment the form is sent, not when the
    // server's render gets back. Otherwise whatever is typed straight after Enter either joins the last line or is
    // wiped by the late clear. Only a form marked ready is cleared, so text the server would refuse isn't lost.
    document.addEventListener("submit", function (e) {
        const form = e.target;
        if (!(form instanceof HTMLFormElement) || form.dataset.clearOnSubmit !== "true") return;
        form.querySelectorAll("[data-clear-on-submit-field]").forEach(function (field) {
            if (field.value.trim()) field.value = "";
        });
    }, true);

    // Ctrl+Enter (Cmd+Enter on a Mac) sends a textarea's form, through the same submit as the button.
    document.addEventListener("keydown", function (e) {
        const field = e.target;
        if (e.key !== "Enter" || !(e.ctrlKey || e.metaKey) || !(field instanceof HTMLTextAreaElement) || !field.hasAttribute("data-submit-on-mod-enter") || !field.form) return;
        e.preventDefault();
        field.form.requestSubmit();
    });

    // Mentions in note boxes: typing @ (a character), # (a place) or $ (an item) offers matching names from the
    // box's data-mentions list. Picking one writes "@Name" into the box; the server turns names into ids when the
    // note is saved. The list lives on <body>, outside anything Blazor renders, so a re-render can't disturb it.
    // A box with templates (data-templates) also offers them after a "/" that starts a word: picking one puts it in.
    // A box with wiki pages (data-pages: [{ n: title, k: kind }]) offers them after "[[": picking one writes "[[Title]]".
    (function () {
        const MAX = 8;
        let list = null, field = null, items = [], active = 0, start = -1;

        function candidates(el, trigger) {
            if (trigger === "[[") {
                if (el._pagesSource !== el.dataset.pages) {
                    el._pagesSource = el.dataset.pages;
                    try { el._pages = JSON.parse(el.dataset.pages || "[]").map(function (p) { return { t: "[[", n: p.n, k: p.k }; }); } catch { el._pages = []; }
                }
                return el._pages;
            }
            if (trigger === "/") {
                return window.codexTemplates.of(el).map(function (t, i) {
                    return { t: "/", n: t.a || t.n, k: t.a ? t.n : "", i: i };
                });
            }
            if (el._mentionsSource !== el.dataset.mentions) {
                el._mentionsSource = el.dataset.mentions;
                try { el._mentions = JSON.parse(el.dataset.mentions || "[]"); } catch { el._mentions = []; }
            }
            return el._mentions;
        }

        // The trigger and query just before the caret: "@ma" in "ask @ma|". Spaces are allowed ("@mara of") while
        // something still matches.
        function query(el) {
            const caret = el.selectionStart, before = el.value.slice(0, caret);
            // An open "[[" on this line, with no "]" after it: the title typed so far. It wins over an "@" or "#" before it,
            // and loses to one typed after it ("[[a @b": the mention).
            let link = null;
            if (el.hasAttribute("data-pages")) {
                const at = before.lastIndexOf("[[");
                const typed = at < 0 ? "" : before.slice(at + 2);
                if (at >= 0 && typed.length <= 60 && !/[\]\n\[]/.test(typed)) link = { trigger: "[[", text: typed, start: at };
            }
            const other = mentionQuery(el, before);
            return other && (!link || other.start > link.start) ? other : link;
        }

        function mentionQuery(el, before) {
            for (let i = before.length - 1; i >= 0 && before.length - i <= 40; i--) {
                const ch = before[i];
                if (ch === "\n") return null;
                if ((ch === "@" || ch === "#" || ch === "$") && (i === 0 || !/[\p{L}\p{N}]/u.test(before[i - 1]))) {
                    return { trigger: ch, text: before.slice(i + 1), start: i };
                }
                // "/" only where a word starts, so "and/or" and a link's "https://" never open the list.
                if (ch === "/" && el.hasAttribute("data-templates") && (i === 0 || /\s/.test(before[i - 1]))) {
                    return { trigger: ch, text: before.slice(i + 1), start: i };
                }
            }
            return null;
        }

        function matches(el, q) {
            const needle = q.text.toLowerCase();
            return candidates(el, q.trigger).filter(function (c) {
                if (c.t !== q.trigger) return false;
                return [c.n, c.t === "/" ? c.k : ""].some(function (text) {
                    const name = text.toLowerCase();
                    return name.startsWith(needle) || name.split(/\s+/).some(function (word) { return word.startsWith(needle); });
                });
            }).slice(0, MAX);
        }

        // Where the caret is inside a textarea, measured on a hidden copy of its text (a textarea doesn't say).
        const COPIED = ["boxSizing", "width", "fontFamily", "fontSize", "fontWeight", "fontStyle", "letterSpacing", "lineHeight", "textTransform", "wordSpacing",
            "textIndent", "tabSize", "paddingTop", "paddingRight", "paddingBottom", "paddingLeft", "borderTopWidth", "borderRightWidth", "borderBottomWidth", "borderLeftWidth"];
        function caretRect(el) {
            const style = getComputedStyle(el), mirror = document.createElement("div");
            COPIED.forEach(function (name) { mirror.style[name] = style[name]; });
            mirror.style.cssText += ";position:absolute;visibility:hidden;top:0;left:-9999px;white-space:pre-wrap;overflow-wrap:break-word;";
            mirror.textContent = el.value.slice(0, el.selectionStart);
            const marker = document.createElement("span");
            marker.textContent = "\u200b";
            mirror.appendChild(marker);
            document.body.appendChild(mirror);
            const height = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.4;
            const rect = { left: marker.offsetLeft, top: marker.offsetTop, height: height };
            mirror.remove();
            return rect;
        }

        function close() {
            if (list) list.hidden = true;
            if (field) {
                field.setAttribute("aria-expanded", "false");
                field.removeAttribute("aria-activedescendant");
            }
            field = null; items = []; start = -1;
        }

        function render() {
            if (!list) {
                list = document.createElement("ul");
                list.id = "mention-list";
                list.className = "mention-list";
                list.setAttribute("role", "listbox");
                list.setAttribute("aria-label", "Suggestions");
                // mousedown, not click: the box keeps focus, so the caret is still where the name goes.
                list.addEventListener("mousedown", function (e) {
                    const option = e.target.closest("[role=option]");
                    if (!option) return;
                    e.preventDefault();
                    choose(Number(option.dataset.index));
                });
                document.body.appendChild(list);
            }
            list.replaceChildren.apply(list, items.map(function (c, i) {
                const li = document.createElement("li");
                li.id = "mention-option-" + i;
                li.setAttribute("role", "option");
                li.setAttribute("aria-selected", i === active ? "true" : "false");
                li.dataset.index = i;
                const name = document.createElement("span");
                name.textContent = c.t === "[[" ? c.n : c.t + c.n;
                const kind = document.createElement("span");
                kind.className = "mention-kind";
                kind.textContent = c.k;
                li.append(name, kind);
                return li;
            }));
            const box = field.getBoundingClientRect();
            list.hidden = false;
            if (box.height > 200) {
                // A tall box (a wiki page): its bottom edge can be off screen, so the list follows the caret instead.
                const caret = caretRect(field);
                const width = Math.min(box.width, 320);
                const line = box.top + caret.top - field.scrollTop;
                list.style.width = width + "px";
                list.style.left = Math.max(box.left, Math.min(box.left + caret.left, box.right - width)) + "px";
                list.style.top = (line + caret.height + 4) + "px";
                if (list.getBoundingClientRect().bottom > window.innerHeight - 8) {
                    list.style.top = Math.max(8, line - list.offsetHeight - 4) + "px";
                }
            } else {
                list.style.left = box.left + "px";
                list.style.width = box.width + "px";
                list.style.top = (box.bottom + 4) + "px";
            }
            field.setAttribute("aria-controls", "mention-list");
            field.setAttribute("aria-expanded", "true");
            field.setAttribute("aria-activedescendant", "mention-option-" + active);
        }

        function update(el) {
            const q = query(el);
            const found = q ? matches(el, q) : [];
            if (!q || found.length === 0) { if (field === el) close(); return; }
            if (field !== el) active = 0;
            field = el; items = found; start = q.start;
            active = Math.min(active, items.length - 1);
            render();
        }

        function choose(index) {
            const c = items[index], el = field;
            if (!c || !el) return;
            const caret = el.selectionStart;
            if (c.t === "/") {
                const from = start;
                close();
                window.codexTemplates.insert(el, c.i, from, caret);
                return;
            }
            // A link closes itself, over a "]]" already after the caret (typed, or left from an earlier title).
            const closing = c.t === "[[" ? (el.value.slice(caret, caret + 2) === "]]" ? 2 : el.value[caret] === "]" ? 1 : 0) : 0;
            // "w": what to write instead of the name, for a name two characters share (the stored id; the server sends it).
            const insert = c.t === "[[" ? "[[" + c.n + "]]" : (c.w || c.t + c.n) + " ";
            el.value = el.value.slice(0, start) + insert + el.value.slice(caret + closing);
            const at = start + insert.length;
            el.setSelectionRange(at, at);
            close();
            // Blazor reads the box from its input event.
            el.dispatchEvent(new Event("input", { bubbles: true }));
        }

        document.addEventListener("input", function (e) {
            if (e.target instanceof HTMLElement && (e.target.hasAttribute("data-mentions") || e.target.hasAttribute("data-templates") || e.target.hasAttribute("data-pages"))) update(e.target);
        });

        // Capture on window, ahead of Blazor and the drawer: while the list is open, its keys are the list's.
        window.addEventListener("keydown", function (e) {
            if (!field || e.target !== field || list.hidden) return;
            if (e.key === "ArrowDown" || e.key === "ArrowUp") {
                active = (active + (e.key === "ArrowDown" ? 1 : -1) + items.length) % items.length;
                render();
            } else if ((e.key === "Enter" && !e.ctrlKey && !e.metaKey) || e.key === "Tab") {
                choose(active);
            } else if (e.key === "Escape") {
                close();
            } else {
                return;
            }
            e.preventDefault();
            e.stopPropagation();
        }, true);

        document.addEventListener("focusout", function (e) { if (e.target === field) close(); });
        document.addEventListener("click", function (e) { if (field && e.target !== field) close(); });
        window.addEventListener("resize", close);
        document.addEventListener("scroll", function () { if (field) render(); }, true);
    })();

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();

// <details class="popover-menu"> menus (header language and phone menu): one open at a time, closed by an
// outside click or Escape, which then returns focus to the menu's summary.
(function () {
    function closeAll(except) {
        document.querySelectorAll("details.popover-menu[open]").forEach(function (d) { if (d !== except) d.open = false; });
    }
    document.addEventListener("click", function (e) {
        const target = e.target instanceof Element ? e.target : null;
        const menu = target ? target.closest("details.popover-menu") : null;
        // Following a link out of a menu (the phone section list) must not leave the menu open on the next page, and
        // choosing an item (Build with AI's recipes) closes the menu behind the dialog it opens.
        closeAll(menu && target.closest(".popover-panel a, .popover-panel .menu-item") ? null : menu);
    });
    document.addEventListener("keydown", function (e) {
        if (e.key !== "Escape") return;
        const open = document.querySelector("details.popover-menu[open]");
        if (!open) return;
        open.open = false;
        open.querySelector("summary")?.focus();
    });
})();

// Phones never show title= tooltips, and some carry rules (a condition's expiry, an initiative roll, death
// saves). On a screen without hover, tapping something that has a title shows it in a bubble; the next tap,
// a scroll or Escape hides it. Controls keep their own tap behaviour: a tap presses them, and a long press
// (half a second) shows what they do instead, so a button with only an icon can be asked before it is pressed.
(function () {
    if (!window.matchMedia || !window.matchMedia("(hover: none)").matches) return;
    let tip = null;
    let press = null;
    let swallowClick = false;
    function hide() { if (tip) { tip.remove(); tip = null; } }
    function labelOf(el) { return (el.getAttribute("title") || el.getAttribute("aria-label") || "").trim(); }
    document.addEventListener("touchstart", function (e) {
        clearTimeout(press);
        const target = e.target instanceof Element ? e.target : null;
        // The control's own name, or the title of what holds it (a condition chip around its name's link).
        const control = target ? target.closest("button, a, summary") : null;
        const el = control && labelOf(control) ? control : target ? target.closest("[title]") : null;
        if (!el || !labelOf(el) || target.closest("input, select, textarea")) return;
        press = setTimeout(function () {
            hide();
            show(el, labelOf(el));
            swallowClick = true;
        }, 500);
    }, { passive: true });
    ["touchend", "touchmove", "touchcancel"].forEach(function (type) {
        document.addEventListener(type, function () {
            clearTimeout(press);
            // A long press may end with no click at all (Android opens its menu instead), so the next tap isn't swallowed.
            if (swallowClick) setTimeout(function () { swallowClick = false; }, 400);
        }, { passive: true });
    });
    // The press that showed the tip doesn't also press the control, nor open the browser's menu for a link.
    document.addEventListener("contextmenu", function (e) { if (swallowClick) e.preventDefault(); });
    document.addEventListener("click", function (e) {
        if (swallowClick) {
            swallowClick = false;
            e.preventDefault();
            e.stopPropagation();
        }
    }, true);
    document.addEventListener("click", function (e) {
        const wasOpen = !!tip;
        hide();
        const target = e.target instanceof Element ? e.target : null;
        const el = target ? target.closest("[title]") : null;
        if (!el || wasOpen || !el.title.trim() || target.closest("button, a, input, select, textarea, label, summary")) return;
        show(el, el.title);
    });
    function show(el, text) {
        tip = document.createElement("div");
        tip.className = "tap-tip";
        tip.setAttribute("role", "tooltip");
        tip.dir = "auto";
        tip.textContent = text;
        document.body.appendChild(tip);
        const r = el.getBoundingClientRect();
        const w = tip.offsetWidth, h = tip.offsetHeight;
        const left = Math.min(Math.max(8, r.left + r.width / 2 - w / 2), window.innerWidth - w - 8);
        const top = r.top - h - 8 > 8 ? r.top - h - 8 : r.bottom + 8;
        tip.style.left = left + "px";
        tip.style.top = top + "px";
    }
    window.addEventListener("scroll", hide, { passive: true, capture: true });
    document.addEventListener("keydown", function (e) { if (e.key === "Escape") hide(); });
})();

// ---- Interop used by components ----
window.codex = {
    // Theme: persisted per browser; the inline <head> script applies it before first paint.
    getTheme: function () {
        return document.documentElement.getAttribute('data-theme') || 'dark';
    },
    setTheme: function (theme) {
        document.documentElement.setAttribute('data-theme', theme);
        document.documentElement.setAttribute('data-bs-theme', theme);
        try { localStorage.setItem('codex-theme', theme); } catch (e) { /* private mode */ }
    },

    // Downloads: Blazor hands over a data URI (or blob URL) plus filename.
    download: function (href, filename) {
        const a = document.createElement('a');
        a.href = href;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        a.remove();
    },

    // Fetches a file the server builds (a printed book can take seconds and can be refused) and saves it, so a refusal is
    // reported on the page instead of replacing it with an error document. Returns the status and the server's reason.
    downloadFrom: async function (url) {
        let response;
        try {
            response = await fetch(url, { credentials: 'same-origin', cache: 'no-store' });
        } catch (e) {
            return { status: 0, detail: null, code: null };
        }
        if (!response.ok) {
            let detail = null, code = null;
            try { const problem = await response.json(); detail = problem.detail || null; code = problem.code || null; } catch (e) { /* not JSON */ }
            return { status: response.status, detail: detail, code: code };
        }
        const blob = await response.blob();
        const disposition = response.headers.get('content-disposition') || '';
        let name = 'book.pdf';
        const star = /filename\*=UTF-8''([^;]+)/i.exec(disposition);
        const plain = /filename="?([^";]+)"?/i.exec(disposition);
        try { if (star) name = decodeURIComponent(star[1]); else if (plain) name = plain[1]; } catch (e) { /* keep the default */ }
        const href = URL.createObjectURL(blob);
        window.codex.download(href, name);
        setTimeout(() => URL.revokeObjectURL(href), 60000);
        return { status: 200, detail: null, code: null };
    },

    copyText: async function (text) {
        // navigator.clipboard only exists on secure origins; a player on http://192.168.x.x has to
        // take the older execCommand route.
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (e) {
            // fall through to the legacy path
        }
        const area = document.createElement('textarea');
        area.value = text;
        area.setAttribute('readonly', '');
        area.style.position = 'fixed';
        area.style.opacity = '0';
        document.body.appendChild(area);
        try {
            area.select();
            return document.execCommand('copy');
        } catch (e) {
            return false;
        } finally {
            area.remove();
        }
    },

    // Native <dialog> helpers for the ConfirmDialog component and other modals.
    showModal: function (id) {
        const el = document.getElementById(id);
        if (el && el.showModal && !el.open) el.showModal();
    },
    // Escape on a guarded dialog doesn't close it; the component's oncancel handler decides (asking before discarding work).
    guardModal: function (id) {
        const el = document.getElementById(id);
        if (el && !el.dataset.guarded) {
            el.dataset.guarded = "true";
            el.addEventListener("cancel", e => e.preventDefault());
        }
    },
    closeModal: function (id) {
        const el = document.getElementById(id);
        if (el && el.close && el.open) el.close();
    },
    focus: function (id) {
        const el = document.getElementById(id);
        if (el) el.focus();
    },
    // Brings a field into view, focused and its text selected (a link to copy by hand).
    selectField: function (id) {
        const el = document.getElementById(id);
        if (!el) return;
        el.scrollIntoView({ block: "center", behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" });
        el.focus({ preventScroll: true });
        if (el.select) el.select();
    },
    // A text box whose value the server changes from outside what the reader typed (AI text, an undo): the box itself is not
    // re-sent its value on every keystroke, so this is the one way such a change reaches it.
    setValue: function (id, value) {
        const el = document.getElementById(id);
        if (el && el.value !== value) el.value = value;
    },
    // A growing log that scrolls inside itself (the world-building chat): keep its end in view while the reader is at the end,
    // so scrolling back up to re-read is not undone by every new word. force pins it again (the reader just sent something).
    followLog: function (id, force) {
        const el = document.getElementById(id);
        if (!el) return;
        if (!el.dataset.follows) {
            el.dataset.follows = "true";
            el.dataset.pinned = "true";
            el.addEventListener("scroll", () => {
                el.dataset.pinned = el.scrollHeight - el.scrollTop - el.clientHeight < 80 ? "true" : "false";
            }, { passive: true });
        }
        if (force) el.dataset.pinned = "true";
        if (el.dataset.pinned === "true") el.scrollTop = el.scrollHeight;
    },
    // The world-building chat's message box: it grows with what is typed (to 40% of the screen, then scrolls), and Enter sends
    // it where there is a keyboard to make new lines with Shift+Enter. On a touch screen Enter is a new line and the Send button
    // sends. Never while an input method is composing a word (Enter picks the word there).
    composer: function (id, sendId) {
        const el = document.getElementById(id);
        if (!el) return;
        const grow = () => {
            el.style.height = "auto";
            el.style.height = Math.min(el.scrollHeight + 2, Math.round(window.innerHeight * 0.4)) + "px";
        };
        if (!el.dataset.composer) {
            el.dataset.composer = "true";
            el.addEventListener("input", grow);
            el.addEventListener("keydown", e => {
                if (e.key !== "Enter" || e.shiftKey || e.isComposing || e.keyCode === 229) return;
                if (!e.ctrlKey && !e.metaKey && !window.matchMedia("(pointer: fine)").matches) return;
                e.preventDefault();
                const send = document.getElementById(sendId);
                if (send && !send.disabled) send.click();
            });
        }
        grow();
    },
    // The reveal and hide buttons the DM's reading view draws on a page's secret blocks. The page's text is markup, not
    // components, so one listener on its container hands each click to the page. Called after every render; the newest
    // reference wins, and the listener is added once.
    secretActions: function (id, ref) {
        const el = document.getElementById(id);
        if (!el) return;
        el._secretRef = ref;
        if (el.dataset.secretActions) return;
        el.dataset.secretActions = "true";
        el.addEventListener("click", e => {
            const b = e.target.closest("[data-secret-block]");
            if (!b || !el.contains(b) || !el._secretRef) return;
            e.preventDefault();
            el._secretRef.invokeMethodAsync("OnSecretAction", parseInt(b.dataset.secretBlock, 10), b.dataset.secretTo);
        });
    },
    // When this browser last opened a page (an ISO time, or null the first time), noting that it is open now (the server's
    // time, as the reveals are stamped with it, so a wrong clock here marks nothing wrongly): what was revealed on it since is
    // marked new. Per browser, and only a convenience: with storage blocked nothing is marked.
    seenSince: function (key, now) {
        try {
            const was = localStorage.getItem(key);
            localStorage.setItem(key, now);
            return was;
        } catch (e) {
            return null;
        }
    },
    // When this browser last opened each of the pages, for the ones it has opened.
    seenAll: function (keys) {
        const out = {};
        try {
            for (const k of keys) {
                const v = localStorage.getItem(k);
                if (v) out[k] = v;
            }
        } catch (e) { /* storage blocked */ }
        return out;
    },
    scrollToId: function (id) {
        const el = document.getElementById(id);
        if (el) el.scrollIntoView({ block: "center", behavior: "smooth" });
    },
    // Bring a panel into view unless all of it already is (beside the chat on a wide screen it usually is).
    revealPanel: function (id) {
        const el = document.getElementById(id);
        if (!el) return;
        const r = el.getBoundingClientRect();
        if (r.top >= 0 && r.bottom <= window.innerHeight) return;
        el.scrollIntoView({ block: "nearest", behavior: "smooth" });
    },
    // MarkdownEditor's toolbar: wrap the selection (or the word "text" when nothing is selected) in a marker, or start
    // each selected line with a prefix. "input" is raised so Blazor's oninput sees the new value, and Undo still works
    // where the browser supports execCommand on a textarea.
    mdWrap: function (id, before, after) {
        const el = document.getElementById(id);
        if (!el || el.readOnly) return;
        const s = el.selectionStart, e = el.selectionEnd;
        const picked = el.value.slice(s, e) || "text";
        mdReplace(el, s, e, before + picked + after);
        el.setSelectionRange(s + before.length, s + before.length + picked.length);
    },
    mdPrefix: function (id, prefix) {
        const el = document.getElementById(id);
        if (!el || el.readOnly) return;
        const v = el.value;
        const s = v.lastIndexOf("\n", el.selectionStart - 1) + 1;
        let e = v.indexOf("\n", el.selectionEnd);
        if (e < 0) e = v.length;
        const lines = v.slice(s, e).split("\n");
        const all = lines.every(function (l) { return l.startsWith(prefix); });
        const text = lines.map(function (l) { return all ? l.slice(prefix.length) : prefix + l.replace(/^(#+ |- )/, ""); }).join("\n");
        mdReplace(el, s, e, text);
        el.setSelectionRange(s, s + text.length);
    },
    // Puts the selected lines (or, with nothing selected, the paragraph at the cursor) in a ":::dm" block, the secret
    // blocks of SecretText.cs; inside a block already, takes it out of it instead. The text keeps its own lines.
    mdSecret: function (id) {
        const el = document.getElementById(id);
        if (!el || el.readOnly) return;
        const v = el.value;
        const lines = v.split("\n");
        const lineAt = function (pos) { return v.slice(0, pos).split("\n").length - 1; };
        const isOpen = function (l) { return /^\s*:::\s*[^\s:]/.test(l); };
        const isClose = function (l) { return /^\s*:::\s*$/.test(l); };
        let first = lineAt(el.selectionStart), last = lineAt(el.selectionEnd);
        // The block the cursor is in, if any: the nearest opening line above with its closing line below.
        let depth = 0, open = -1;
        for (let i = first; i >= 0; i--) {
            if (isClose(lines[i]) && i !== first) depth++;
            else if (isOpen(lines[i])) { if (depth === 0) { open = i; break; } depth--; }
        }
        if (open >= 0) {
            let close = -1; depth = 0;
            for (let i = open + 1; i < lines.length; i++) {
                if (isOpen(lines[i])) depth++;
                else if (isClose(lines[i])) { if (depth === 0) { close = i; break; } depth--; }
            }
            if (close >= first || close < 0) {
                const end = close < 0 ? lines.length : close + 1;
                const inner = lines.slice(open + 1, close < 0 ? lines.length : close).join("\n");
                const s = lines.slice(0, open).join("\n").length + (open > 0 ? 1 : 0);
                const e = lines.slice(0, end).join("\n").length;
                mdReplace(el, s, e, inner);
                el.setSelectionRange(s, s + inner.length);
                return;
            }
        }
        if (el.selectionStart === el.selectionEnd) {
            while (first > 0 && lines[first - 1].trim() !== "") first--;
            while (last < lines.length - 1 && lines[last + 1].trim() !== "") last++;
        }
        const s = lines.slice(0, first).join("\n").length + (first > 0 ? 1 : 0);
        const e = lines.slice(0, last + 1).join("\n").length;
        const picked = v.slice(s, e);
        const text = ":::dm\n" + (picked.trim() === "" ? "" : picked) + "\n:::";
        mdReplace(el, s, e, text);
        el.setSelectionRange(s + 6, s + 6 + (picked.trim() === "" ? 0 : picked.length));
    },
    // Puts text in at the box's caret (or over its selection), with a space either side where a word would touch it.
    mdInsert: function (id, text) {
        const el = document.getElementById(id);
        if (!el || el.readOnly) return;
        const start = el.selectionStart, end = el.selectionEnd, v = el.value;
        const before = start > 0 && !/\s/.test(v[start - 1]) ? " " : "";
        const after = end < v.length && !/\s/.test(v[end]) ? " " : "";
        mdReplace(el, start, end, before + text + after);
        const at = start + before.length + text.length;
        el.setSelectionRange(at, at);
    },
    // Puts template number <index> of the box's data-templates in at the caret (the editor's template menu, a new page).
    mdTemplate: function (id, index) {
        const el = document.getElementById(id);
        document.querySelectorAll("details.md-templates[open]").forEach(function (d) { d.open = false; });
        if (!el || el.readOnly) return;
        el.focus();
        window.codexTemplates.insert(el, index, el.selectionStart, el.selectionEnd);
    },
    // A detail pane that is a full-screen sheet on a phone (.phone-sheet): move focus into it when it opens,
    // so keyboards and screen readers follow. Beside the list on a wide screen it is left alone.
    // Opening and closing a Library entry are navigations, and Blazor scrolls to the top after each one;
    // sheetClosed puts the list back where it was when the entry was opened.
    focusSheet: function (id) {
        // Following a link inside an open entry (to another entry) keeps the list position of the first one.
        if (sheetListScroll === null) sheetListScroll = lastLinkScroll;
        const el = document.getElementById(id);
        const sheet = el && el.closest(".phone-sheet");
        if (!sheet || getComputedStyle(sheet).position !== "fixed") return;
        sheet.scrollTop = 0;
        el.focus({ preventScroll: true });
    },
    sheetClosed: function () {
        if (sheetListScroll === null) return;
        window.scrollTo({ top: sheetListScroll, behavior: "instant" });
        sheetListScroll = null;
    },
    // A character sheet or a session opens as a window over the section it came from, which stays on the page, hidden
    // (CampaignPage, U2). The window starts at the top; closing it puts the section back where it was scrolled when the
    // click that opened the window happened (by then Blazor has already scrolled for the navigation).
    windowOpened: function () {
        windowUnderScroll = lastClickScroll;
        window.scrollTo({ top: 0, behavior: "instant" });
    },
    windowClosed: function (restore) {
        if (restore) window.scrollTo({ top: windowUnderScroll, behavior: "instant" });
    },
    // A link to a place (".../world#place-ID") lands on its row with its description open, not folded away (U2).
    revealPlace: function (id) {
        const row = document.getElementById(id);
        if (!row) return false;
        const details = row.querySelector("details.place-details");
        if (details) details.open = true;
        row.classList.remove("place-target");
        void row.offsetWidth;
        row.classList.add("place-target");
        row.scrollIntoView({ block: "center", behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" });
        return true;
    },
    // The place on a character sheet that fixes a rule problem (data-rule-key, ActorSheet U4): in view, its first control focused.
    showRule: function (key) {
        const place = Array.from(document.querySelectorAll("[data-rule-key]")).find(function (el) { return el.getAttribute("data-rule-key") === key; });
        if (!place) return;
        place.scrollIntoView({ block: "center", behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" });
        const control = place.querySelector("input:not([type=hidden]):not([disabled]), select:not([disabled]), textarea:not([disabled]), button:not([disabled])");
        if (control) control.focus({ preventScroll: true });
        place.classList.remove("rule-target");
        void place.offsetWidth;
        place.classList.add("rule-target");
    }
};

function mdReplace(el, start, end, text) {
    el.focus();
    el.setSelectionRange(start, end);
    if (!document.execCommand || !document.execCommand("insertText", false, text)) {
        el.setRangeText(text, start, end, "end");
        el.dispatchEvent(new Event("input", { bubbles: true }));
    }
}

// The letter a Ctrl/Cmd shortcut was pressed with. On a Latin layout that is the key's own letter (so Dvorak's S is S); on any
// other (Russian, Hebrew) the key gives "ы" or "ד", so the letter is taken from where the key sits (e.code "KeyS"), as browsers
// and desktop programs do: Ctrl+S saves whatever the layout.
function shortcutKey(e) {
    const key = (e.key || "").toLowerCase();
    if (/^[a-z]$/.test(key)) return key;
    return /^Key[A-Z]$/.test(e.code || "") ? e.code.slice(3).toLowerCase() : key;
}

// Ctrl/Cmd+Shift+H in a MarkdownEditor box that takes secret blocks.
document.addEventListener("keydown", function (e) {
    const el = e.target;
    if (!(el instanceof HTMLTextAreaElement) || !el.hasAttribute("data-md-secrets") || !(e.ctrlKey || e.metaKey) || !e.shiftKey || e.altKey) return;
    if (shortcutKey(e) !== "h") return;
    e.preventDefault();
    window.codex.mdSecret(el.id);
});

// Ctrl/Cmd+B and Ctrl/Cmd+I in a MarkdownEditor box.
document.addEventListener("keydown", function (e) {
    const el = e.target;
    if (!(el instanceof HTMLTextAreaElement) || !el.hasAttribute("data-md") || !(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return;
    const key = shortcutKey(e);
    if (key !== "b" && key !== "i") return;
    e.preventDefault();
    if (key === "b") window.codex.mdWrap(el.id, "**", "**"); else window.codex.mdWrap(el.id, "*", "*");
});

// Ctrl+S (Cmd+S on a Mac) presses the Save of what is being edited: the nearest [data-save-shortcut] button around the focus
// (a page can hold several editors, a session's recap and summary), else the only one on screen; an open dialog's own first.
// The browser's "Save page as" never opens over an editor. A template being filled ends first (its unfilled names come out), and
// a field that sends its value on change (not on input) is blurred, so the text is final before Save runs; focus goes back
// afterwards. Holding the keys down saves once: the key's repeats are swallowed.
document.addEventListener("keydown", function (e) {
    if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey || shortcutKey(e) !== "s") return;
    function usable(b) { return !b.disabled && b.getClientRects().length > 0; }
    const dialog = Array.from(document.querySelectorAll("dialog[open]")).pop();
    const scope = dialog || document;
    let button = null, around = scope;
    for (let node = e.target instanceof Element ? e.target : null; node && !button && node !== document.body; node = node.parentElement) {
        if (dialog && !dialog.contains(node)) break;
        button = Array.from(node.querySelectorAll("[data-save-shortcut]")).find(usable) || null;
        if (button) around = node;
    }
    if (!button) {
        const all = Array.from(scope.querySelectorAll("[data-save-shortcut]")).filter(usable);
        if (all.length === 1) button = all[0];
    }
    if (!button) return;
    e.preventDefault();
    if (e.repeat) return;
    window.codexTemplates.stopWithin(around);
    const field = document.activeElement;
    if (field instanceof HTMLInputElement || field instanceof HTMLSelectElement) field.blur();
    button.click();
    if (field instanceof HTMLElement && field !== document.body) field.focus({ preventScroll: true });
});

// Where the page was scrolled when a link was last followed (captured before Blazor handles the click).
let lastLinkScroll = 0;
let sheetListScroll = null;
// The same for any click: a button can open a window too (Create opens the new character's sheet).
let lastClickScroll = 0;
let windowUnderScroll = 0;
document.addEventListener("click", function (e) {
    lastClickScroll = window.scrollY;
    if (e.target instanceof Element && e.target.closest("a[href]")) lastLinkScroll = window.scrollY;
}, true);

// Escape closes a detail that is a full-screen sheet on a phone (.phone-sheet), as its Back arrow does (U2). Not while typing
// (a box's own Escape: a suggestion list, a template, full screen), and not under an open dialog or menu, which take it first.
document.addEventListener("keydown", function (e) {
    if (e.key !== "Escape" || e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey) return;
    const target = e.target;
    if (target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement
        || (target instanceof HTMLInputElement && target.type !== "checkbox" && target.type !== "radio")) return;
    if (document.querySelector("dialog[open], details.popover-menu[open]")) return;
    const back = Array.from(document.querySelectorAll(".phone-sheet .sheet-bar > .icon-btn")).find(b => b.getClientRects().length > 0);
    if (!back) return;
    e.preventDefault();
    back.click();
});

// Kept for backwards compatibility with earlier builds.
window.codexDownload = window.codex.download;

// Live templates (LiveTemplate.cs) in a box with data-templates: [{ a: abbreviation, n: name, b: body }]. Typing ";" and an
// abbreviation, then Tab, puts its template in; then Tab and Shift+Tab step through its fields, a field used twice repeats what
// is typed in the first, and Tab after the last field (or Escape, or leaving the box, or saving) ends it. Tab anywhere else
// still moves focus, so a keyboard user is never trapped. As in an IDE, a field with no default holds its name, greyed and
// selected, so typing replaces it; a name still there when the template ends is taken out again, so it never reaches the
// page. The fields are drawn over the box (a layer that copies the box's text and wrapping, with only the fields showing),
// and a hint (data-template-hint, with {0} the field, {1} its number and {2} how many) says where the caret is.
window.codexTemplates = (function () {
    // Typed before an abbreviation to put its template in with Tab; keep in step with LiveTemplate.Prefix.
    const PREFIX = ";";
    // Keep in step with Token in LiveTemplate.cs.
    const TOKEN = /\$\$|\$([\p{L}\p{N}_]+)(?::([^$\r\n]*))?\$/gu;
    // What the layer over the box copies from it, so its text wraps line for line with the box's.
    const COPIED = ["fontFamily", "fontSize", "fontWeight", "fontStyle", "fontVariant", "fontStretch", "fontFeatureSettings", "fontVariationSettings",
        "fontKerning", "letterSpacing", "wordSpacing", "lineHeight", "textTransform", "textIndent", "textAlign", "direction", "unicodeBidi", "tabSize",
        "whiteSpace", "overflowWrap", "wordBreak", "paddingTop", "paddingRight", "paddingBottom", "paddingLeft"];
    let s = null;
    let hint = null;
    let layer = null;
    let mirroring = false;
    let frame = 0;
    const resized = window.ResizeObserver ? new ResizeObserver(function () { later(); }) : null;

    function of(el) {
        if (el._templatesSource !== el.dataset.templates) {
            el._templatesSource = el.dataset.templates;
            try { el._templates = JSON.parse(el.dataset.templates || "[]"); } catch { el._templates = []; }
        }
        return el._templates;
    }

    function label(name) { return name.replace(/_/g, " "); }

    // The template with each field holding its default; with <placeholders>, a field with none holds its name instead (ph).
    function expand(body, placeholders) {
        // A Map, not an object: a field called "constructor" or "toString" must not find the object's own.
        const defaults = new Map();
        for (const m of body.matchAll(TOKEN)) {
            if (m[1] !== undefined && m[2] !== undefined && !defaults.has(m[1])) defaults.set(m[1], m[2]);
        }
        let text = "", at = 0, end = -1;
        const fields = [], seen = new Set();
        for (const m of body.matchAll(TOKEN)) {
            text += body.slice(at, m.index);
            at = m.index + m[0].length;
            if (m[1] === undefined) { text += "$"; continue; }
            if (m[1] === "END") { if (end < 0) end = text.length; continue; }
            const value = defaults.get(m[1]) || "";
            const ph = !value && !!placeholders;
            const shown = ph ? label(m[1]) : value;
            fields.push({ name: m[1], start: text.length, end: text.length + shown.length, mirror: seen.has(m[1]), ph: ph });
            seen.add(m[1]);
            text += shown;
        }
        text += body.slice(at);
        return { text: text, fields: fields, end: end < 0 ? text.length : end };
    }

    function insert(el, index, from, to) {
        const t = of(el)[index];
        if (!t || el.readOnly) return;
        // A template put in while another is being filled ends that one first; taking its names out moves what comes after.
        stop().forEach(function (r) {
            from = moved(from, r);
            to = moved(to, r);
        });
        const x = expand(t.b.replace(/\r\n/g, "\n"), true);
        mdReplace(el, from, to, x.text);
        const fields = x.fields.map(function (f) { return { name: f.name, start: f.start + from, end: f.end + from, mirror: f.mirror, ph: f.ph }; });
        const stops = fields.filter(function (f) { return !f.mirror; });
        if (stops.length === 0) {
            el.setSelectionRange(from + x.end, from + x.end);
            return;
        }
        s = { el: el, fields: fields, stops: stops, end: from + x.end, index: 0, value: el.value };
        if (resized) resized.observe(el);
        go(0);
    }

    // Where a position ends up once the text from r.start to r.end is taken out.
    function moved(p, r) {
        if (p >= r.end) return p - (r.end - r.start);
        return p > r.start ? r.start : p;
    }

    function go(i) {
        s.index = i;
        const f = s.stops[i];
        s.el.setSelectionRange(f.start, f.end);
        show();
        follow();
    }

    // The part of the screen the box can be read in: under the site's header, above the editor's Save row when that sticks.
    function readable() {
        const top = parseFloat(getComputedStyle(document.documentElement).getPropertyValue("--header-h")) || 0;
        let bottom = window.innerHeight;
        const form = s.el.closest("form");
        const bar = form && form.querySelector(".save-bar");
        if (bar) {
            const r = bar.getBoundingClientRect();
            if (r.top > top && r.top < bottom) bottom = r.top;
        }
        return { top: top, bottom: bottom };
    }

    // Brings the field being filled into view, as an IDE moves to it: in the box first, then the page (on a phone the box is
    // mostly under the Save row and the tab bar). Where the field is comes from the layer, which lays the text out as the box does.
    function follow() {
        const mark = layer && layer.querySelector(".tf-current");
        if (!mark) return;
        const box = s.el.getBoundingClientRect();
        let r = mark.getBoundingClientRect();
        if (r.top < box.top || r.bottom > box.bottom) {
            s.el.scrollTop += r.top - box.top - Math.max(0, (box.height - r.height) / 3);
            paint();
            r = layer.querySelector(".tf-current").getBoundingClientRect();
        }
        const view = readable();
        if (r.top < view.top + 8 || r.bottom > view.bottom - 8) {
            const reduce = window.matchMedia && matchMedia("(prefers-reduced-motion: reduce)").matches;
            window.scrollBy({ top: r.top - view.top - Math.max(0, (view.bottom - view.top - r.height) / 3), behavior: reduce ? "auto" : "smooth" });
        }
    }

    // Ends the template being filled and takes out the names still standing in fields no one typed in, unless <keep> (the box
    // was changed from outside, so where the fields are is not known). Returns the ranges taken out, last first.
    function stop(keep) {
        const was = s;
        s = null;
        if (hint) { hint.hidden = true; hint.firstChild.textContent = ""; }
        if (layer) { layer.hidden = true; layer.firstChild.textContent = ""; }
        if (resized) resized.disconnect();
        if (!was || keep || was.el.value !== was.value) return [];
        const gone = was.fields.filter(function (f) { return f.ph && f.end > f.start; })
            .sort(function (a, b) { return b.start - a.start; })
            .map(function (f) { return { start: f.start, end: f.end }; });
        if (gone.length === 0) return gone;
        const el = was.el;
        gone.forEach(function (r) { el.setRangeText("", r.start, r.end, "preserve"); });
        el.dispatchEvent(new Event("input", { bubbles: true }));
        return gone;
    }

    function finish() {
        s.el.setSelectionRange(s.end, s.end);
        stop();
    }

    // The hint's buttons step back, step on and end the template, for a phone or tablet whose keyboard has no Tab or Escape
    // (and for the mouse). They never take the focus, which would end the template: the box keeps it, and its keyboard stays up.
    const ICONS = {
        prev: "<path d=\"m15 18-6-6 6-6\"/>",
        next: "<path d=\"m9 18 6-6-6-6\"/>",
        done: "<path d=\"M20 6 9 17l-5-5\"/>"
    };

    function button(kind, title, act) {
        const b = document.createElement("button");
        b.type = "button";
        b.tabIndex = -1;
        b.className = "template-hint-btn th-" + kind;
        b.title = title;
        b.setAttribute("aria-label", title);
        b.dataset.testid = "template-" + kind;
        b.innerHTML = "<svg viewBox=\"0 0 24 24\" width=\"16\" height=\"16\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">" + ICONS[kind] + "</svg>";
        b.addEventListener("click", function () { if (s) act(); });
        return b;
    }

    function show() {
        if (!hint) {
            hint = document.createElement("div");
            hint.className = "template-hint";
            hint.dataset.testid = "template-hint";
            const text = document.createElement("span");
            text.className = "template-hint-text";
            text.setAttribute("role", "status");
            text.setAttribute("aria-live", "polite");
            hint.appendChild(text);
            hint.addEventListener("pointerdown", function (e) { e.preventDefault(); });
            hint.addEventListener("mousedown", function (e) { e.preventDefault(); });
            document.body.appendChild(hint);
        }
        const names = (s.el.dataset.templateButtons || "").split("|");
        if (hint.dataset.buttons !== s.el.dataset.templateButtons) {
            hint.dataset.buttons = s.el.dataset.templateButtons || "";
            hint.querySelectorAll("button").forEach(function (b) { b.remove(); });
            hint.appendChild(button("prev", names[0] || "‹", function () { go(Math.max(0, s.index - 1)); }));
            hint.appendChild(button("next", names[1] || "›", function () { if (s.index + 1 < s.stops.length) go(s.index + 1); else finish(); }));
            hint.appendChild(button("done", names[2] || "✓", function () { stop(); }));
        }
        const f = s.stops[s.index];
        // Without a hardware keyboard the keys the hint names are not there: it says only where the caret is.
        const touch = window.matchMedia && matchMedia("(hover: none) and (pointer: coarse)").matches;
        const words = (touch && s.el.dataset.templateHintTouch) || s.el.dataset.templateHint || "{0} ({1}/{2})";
        hint.firstChild.textContent = words.replace("{0}", label(f.name)).replace("{1}", s.index + 1).replace("{2}", s.stops.length);
        hint.querySelector(".th-prev").disabled = s.index === 0;
        hint.hidden = false;
        place();
        paint();
    }

    // The hint in the box's bottom corner on the end side (left in Hebrew), or the bottom of the screen when that is out of view;
    // above the editor's Save row when that sticks over the box, so the hint never covers its buttons.
    function place() {
        const box = s.el.getBoundingClientRect();
        const rtl = getComputedStyle(s.el).direction === "rtl";
        const left = rtl ? box.left + 18 : box.right - hint.offsetWidth - 18;
        const bottom = Math.min(box.bottom, readable().bottom);
        hint.style.left = Math.max(8, Math.min(left, window.innerWidth - hint.offsetWidth - 8)) + "px";
        hint.style.top = Math.max(8, bottom - hint.offsetHeight - 8) + "px";
    }

    function escape(text) {
        return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    }

    // Above the box's own layer: one more than the highest z-index among its positioned ancestors (a full-screen editor's).
    function zOver(el) {
        let z = 0;
        for (let node = el.parentElement; node && node !== document.body; node = node.parentElement) {
            const cs = getComputedStyle(node);
            if (cs.position !== "static" && cs.zIndex !== "auto") z = Math.max(z, parseInt(cs.zIndex, 10) || 0);
        }
        return z + 1;
    }

    // Draws the fields over the box: the box's text again, invisible, with each field outlined, the one being filled lit,
    // and a name standing in a field drawn greyed over the box's own letters (it covers them with the box's background).
    function paint() {
        const el = s.el;
        if (!layer) {
            layer = document.createElement("div");
            layer.className = "template-marks";
            layer.setAttribute("aria-hidden", "true");
            layer.appendChild(document.createElement("div"));
            document.body.appendChild(layer);
        }
        const cs = getComputedStyle(el);
        const box = el.getBoundingClientRect();
        layer.hidden = false;
        layer.style.left = (box.left + window.scrollX + el.clientLeft) + "px";
        layer.style.top = (box.top + window.scrollY + el.clientTop) + "px";
        layer.style.width = el.clientWidth + "px";
        layer.style.height = el.clientHeight + "px";
        layer.style.zIndex = zOver(el);
        layer.style.setProperty("--tf-bg", cs.backgroundColor);
        const inner = layer.firstChild;
        COPIED.forEach(function (p) { inner.style[p] = cs[p]; });
        inner.style.width = el.clientWidth + "px";
        inner.style.transform = "translate(" + (-el.scrollLeft) + "px, " + (-el.scrollTop) + "px)";
        const value = el.value, current = s.stops[s.index].name;
        let html = "", at = 0;
        s.fields.slice().sort(function (a, b) { return a.start - b.start; }).forEach(function (f) {
            if (f.start < at) return;
            html += escape(value.slice(at, f.start));
            const kind = "tf" + (f.ph ? " tf-ph" : "") + (f.name === current ? " tf-current" : "") + (f.end === f.start ? " tf-empty" : "");
            html += "<span class=\"" + kind + "\">" + escape(value.slice(f.start, f.end)) + "</span>";
            at = f.end;
        });
        // A last empty line still takes its height in the box.
        inner.innerHTML = html + escape(value.slice(at)) + "\n";
    }

    function later() {
        if (!s || frame) return;
        frame = requestAnimationFrame(function () {
            frame = 0;
            if (s) { place(); paint(); }
        });
    }

    // The edit just made: the old text from p to oldEnd became the new text from p to newEnd. Typing, deleting and pasting
    // all leave the caret at the end of what changed, which settles where a run of equal letters was typed.
    function change(before, after, caret) {
        const tail = after.length - caret;
        let p = 0;
        const limit = Math.min(before.length, after.length);
        while (p < limit && before[p] === after[p]) p++;
        if (tail >= 0 && tail <= before.length && before.slice(before.length - tail) === after.slice(caret)) {
            return { p: Math.min(p, caret, before.length - tail), oldEnd: before.length - tail, newEnd: caret };
        }
        let q = 0;
        while (q < limit - p && before[before.length - 1 - q] === after[after.length - 1 - q]) q++;
        return { p: p, oldEnd: before.length - q, newEnd: after.length - q };
    }

    function shift(from, delta, except) {
        s.fields.forEach(function (f) {
            if (f === except) return;
            if (f.start >= from) f.start += delta;
            if (f.end >= from) f.end += delta;
        });
        if (s.end >= from) s.end += delta;
    }

    function edited(el) {
        const c = change(s.value, el.value, el.selectionStart);
        const delta = (c.newEnd - c.p) - (c.oldEnd - c.p);
        // The field the edit is in: the one being filled first, then any (a click into another before typing).
        const inside = function (f) { return c.p >= f.start && c.oldEnd <= f.end; };
        const field = inside(s.stops[s.index]) ? s.stops[s.index] : s.fields.find(inside);
        if (!field) {
            // Typed outside the fields: that ends the template, the fields after the edit moved by it.
            shift(c.oldEnd, delta, null);
            s.value = el.value;
            stop();
            return;
        }
        const oldEnd = field.end;
        field.end += delta;
        shift(oldEnd, delta, field);
        // Typed in: what the field holds is the DM's now, in every use of it.
        s.fields.forEach(function (f) { if (f.name === field.name) f.ph = false; });
        const value = el.value.slice(field.start, field.end);
        let caret = el.selectionStart;
        // Repeat it in the field's other uses, last first so the earlier positions stay right.
        s.fields.filter(function (f) { return f !== field && f.name === field.name; })
            .sort(function (a, b) { return b.start - a.start; })
            .forEach(function (m) {
                const d = value.length - (m.end - m.start);
                if (d === 0 && el.value.slice(m.start, m.end) === value) return;
                el.setRangeText(value, m.start, m.end, "preserve");
                const mEnd = m.end;
                m.end = m.start + value.length;
                shift(mEnd, d, m);
                if (m.start < caret) caret += d;
            });
        if (el.value !== s.value) {
            el.setSelectionRange(caret, caret);
            mirroring = true;
            el.dispatchEvent(new Event("input", { bubbles: true }));
            mirroring = false;
        }
        // Typing in a repeat of a field counts as being in the field.
        s.index = s.stops.findIndex(function (f) { return f.name === field.name; });
        s.value = el.value;
        show();
    }

    document.addEventListener("input", function (e) {
        if (!s || mirroring || e.target !== s.el) return;
        edited(s.el);
    });

    // Leaving the box ends the template, but the window losing the focus (another program, another tab) does not: the box
    // gets it back on return.
    document.addEventListener("focusout", function (e) {
        if (s && e.target === s.el && document.hasFocus()) stop();
    });
    // Saving ends it too, before the form sends the text (Ctrl+S presses Save without leaving the box).
    window.addEventListener("submit", function (e) {
        if (s && e.target instanceof Element && e.target.contains(s.el)) stop();
    }, true);
    window.addEventListener("resize", later);
    window.addEventListener("scroll", later, true);

    // On window, after the suggestion list (which takes Tab and Escape first while it is open).
    window.addEventListener("keydown", function (e) {
        const el = e.target;
        if (e.defaultPrevented || !(el instanceof HTMLTextAreaElement) || e.ctrlKey || e.metaKey || e.altKey) return;
        if (s && el === s.el && el.value !== s.value) stop(true); // changed from outside (the AI, a re-render)
        if (s && el === s.el) {
            if (e.key === "Escape") {
                stop();
            } else if (e.key === "Tab") {
                // Shift+Tab before the first field leaves the template, and the box, the way Shift+Tab leaves any box.
                if (e.shiftKey && s.index === 0) { stop(); return; }
                if (e.shiftKey) go(s.index - 1);
                else if (s.index + 1 < s.stops.length) go(s.index + 1);
                else finish();
            } else {
                return;
            }
            e.preventDefault();
            e.stopPropagation();
            return;
        }
        if (e.key !== "Tab" || e.shiftKey || !el.hasAttribute("data-templates") || el.readOnly || el.selectionStart !== el.selectionEnd) return;
        // Only after the prefix (";npc"), so an abbreviation that is also a word ("npc" in a sentence) followed by Tab still moves on.
        const before = el.value.slice(0, el.selectionStart);
        const word = /(?:^|[^\p{L}\p{N}_-]);([\p{L}\p{N}_-]+)$/u.exec(before);
        if (!word) return;
        const typed = word[1].toLowerCase();
        const index = of(el).findIndex(function (t) { return t.a && t.a.toLowerCase() === typed; });
        if (index < 0) return;
        e.preventDefault();
        e.stopPropagation();
        insert(el, index, el.selectionStart - word[1].length - PREFIX.length, el.selectionStart);
    }, true);

    // Ends the template being filled when its box is inside <scope> (Save pressed for that editor), names left standing taken out.
    function stopWithin(scope) {
        if (s && scope && scope.contains(s.el)) stop();
    }

    return { of: of, insert: insert, expand: expand, stopWithin: stopWithin };
})();

// The preview card (U1): a mention, a [[link]] or a name on the table (a[data-preview]) shows a card with the name, what it is,
// its first lines, its picture and what it mentions, from /campaigns/{id}/preview (PreviewEndpoints.cs), which answers only
// what the viewer may read. Answers are kept a minute: a reveal shows soon after.
// - With a mouse, hovering (or focusing) shows the card. The pointer can move onto it; a name on it opens the next card beside
//   it, and the pin keeps a card open while you read on, until its close button or Escape.
// - On a touch screen, a tap opens the card as a sheet at the bottom instead of following the link, with Open to go there; a
//   name on the sheet opens its card in the same sheet, and Back returns. When there is no card to show, the tap follows the link.
(function () {
    if (!window.matchMedia) return;
    const HOVER = window.matchMedia("(hover: hover)").matches;
    const SHOW_AFTER = 350, HIDE_AFTER = 300, KEEP = 60000, MAX_PINNED = 4;
    const SELECTOR = "a.mention, a.wiki-link:not(.wiki-missing), a[data-preview]";
    const ICONS = {
        pin: "<path d=\"M12 17v5\"/><path d=\"M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z\"/>",
        close: "<path d=\"M18 6 6 18\"/><path d=\"m6 6 12 12\"/>",
        back: "<path d=\"m12 19-7-7 7-7\"/><path d=\"M19 12H5\"/>"
    };
    const cache = new Map();
    let serial = 0;

    function text(key) {
        const s = document.getElementById("codex-preview-strings");
        return (s && s.getAttribute("data-" + key)) || "";
    }

    // The preview a link asks for, or null for a link that has none.
    function target(link) {
        let url;
        try { url = new URL(link.href, location.href); } catch { return null; }
        if (url.origin !== location.origin) return null;
        let m = url.pathname.match(/^\/campaigns\/([^/]+)\/actors\/([^/]+)$/);
        if (m) return "/campaigns/" + m[1] + "/preview/actor/" + m[2];
        m = url.pathname.match(/^\/campaigns\/([^/]+)\/wiki\/([^/]+)$/);
        if (m && m[2] !== "new") return "/campaigns/" + m[1] + "/preview/page/" + m[2];
        m = url.pathname.match(/^\/campaigns\/([^/]+)\/world$/);
        if (m && url.hash.startsWith("#place-")) return "/campaigns/" + m[1] + "/preview/place/" + url.hash.slice(7);
        m = url.pathname.match(/^\/campaigns\/([^/]+)\/library\/([^/]+)\/([^/]+)$/);
        if (m) return "/campaigns/" + m[1] + "/preview/entry/" + m[2] + "/" + m[3];
        m = url.pathname.match(/^\/campaigns\/([^/]+)\/sessions\/([^/]+)$/);
        if (m) return "/campaigns/" + m[1] + "/preview/session/" + m[2];
        return null;
    }

    // Inside the DM's "view as players" (a page part, or a card opened from one), the card answers as it would for a player
    // (the server checks it is the DM asking).
    function source(link) {
        const src = target(link);
        return src && link.closest("[data-view-as='players']") ? src + "?as=players" : src;
    }

    function load(src) {
        const hit = cache.get(src);
        if (hit && Date.now() - hit.at < KEEP) return hit.data;
        const data = fetch(src, { credentials: "same-origin", headers: { Accept: "application/json", "X-Time-Zone": window.codexTimeZone() } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; });
        cache.set(src, { at: Date.now(), data: data });
        return data;
    }

    function icon(name) {
        return "<svg viewBox=\"0 0 24 24\" width=\"16\" height=\"16\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">" + ICONS[name] + "</svg>";
    }

    function button(name, label, testid) {
        const b = document.createElement("button");
        b.type = "button";
        b.className = "hover-card-btn";
        b.innerHTML = icon(name);
        b.setAttribute("aria-label", label);
        b.title = label;
        b.dataset.testid = testid;
        return b;
    }

    // The card's content: picture, name (a link to it), what it is, the first lines, and the names it mentions as links that
    // open their own cards.
    function content(p, src) {
        const id = "hover-card-" + (++serial);
        const parts = [];
        if (p.image) {
            const img = document.createElement("img");
            img.src = p.image; img.alt = ""; img.className = "hover-card-img";
            parts.push(img);
        }
        const body = document.createElement("div");
        body.className = "hover-card-body";
        const title = document.createElement("a");
        title.className = "hover-card-title"; title.href = p.href; title.textContent = p.title; title.id = id + "-title";
        const sub = document.createElement("span");
        sub.className = "hover-card-sub"; sub.textContent = p.sub;
        body.append(title, sub);
        if (p.text) {
            const t = document.createElement("p");
            t.className = "hover-card-text"; t.textContent = p.text;
            body.append(t);
        }
        if (p.links && p.links.length) {
            const list = document.createElement("p");
            list.className = "hover-card-links";
            const label = document.createElement("span");
            label.className = "hover-card-links-label"; label.textContent = text("names");
            list.append(label);
            p.links.forEach(function (l) {
                const a = document.createElement("a");
                a.href = l.href; a.textContent = l.title; a.dataset.preview = ""; a.className = "hover-card-link";
                list.append(" ", a);
            });
            body.append(list);
        }
        parts.push(body);
        const card = document.createElement("div");
        card.id = id;
        card.className = "hover-card";
        card.dir = "auto";
        card.setAttribute("role", "dialog");
        card.setAttribute("aria-labelledby", title.id);
        card.dataset.testid = "hover-card";
        if (src.indexOf("?as=players") > 0) card.dataset.viewAs = "players";
        card.append.apply(card, parts);
        return card;
    }

    function campaignOf(path) {
        const m = path.match(/^\/campaigns\/[^/?#]+/);
        return m ? m[0] : "";
    }

    if (HOVER) desktop(); else touch();

    // ---- With a mouse: cards beside the link, the pointer can move onto them, a pin keeps them. ----
    function desktop() {
        const cards = []; // { el, link, src, pinned }, a name on card i opening card i + 1
        let showTimer = 0, hideTimer = 0, pending = null;

        function indexOf(el) {
            const card = el instanceof Element ? el.closest(".hover-card") : null;
            return card ? cards.findIndex(function (c) { return c.el === card; }) : -1;
        }

        function close(i) {
            const c = cards[i];
            if (!c) return;
            cards.splice(i, 1);
            c.el.remove();
            if (c.link.getAttribute("aria-describedby") === c.el.id) c.link.removeAttribute("aria-describedby");
        }

        // Every card after card `keep` that is not pinned (all of them for -1), and their pending show.
        function trim(keep) {
            for (let i = cards.length - 1; i > keep; i--) if (!cards[i].pinned) close(i);
        }

        function cancel() {
            clearTimeout(showTimer);
            pending = null;
        }

        function laterTrim(keep) {
            clearTimeout(hideTimer);
            hideTimer = setTimeout(function () { trim(keep); }, HIDE_AFTER);
        }

        function place(c, from) {
            const el = c.el, w = el.offsetWidth, h = el.offsetHeight, gap = 6;
            const fits = function (x) { return x >= 8 && x + w <= window.innerWidth - 8; };
            let left = null, top;
            if (from >= 0) {
                // Beside the card the name is on, at the name's height; under the name when neither side has room.
                const r = cards[from].el.getBoundingClientRect();
                const rtl = getComputedStyle(cards[from].el).direction === "rtl";
                const after = rtl ? r.left - w - gap : r.right + gap, before = rtl ? r.right + gap : r.left - w - gap;
                left = fits(after) ? after : fits(before) ? before : null;
                top = c.link.getBoundingClientRect().top - 12;
            }
            if (left === null) {
                const r = c.link.getBoundingClientRect();
                left = r.left;
                top = r.bottom + gap + h > window.innerHeight - 8 && r.top - h - gap > 8 ? r.top - h - gap : r.bottom + gap;
            }
            el.style.left = Math.min(Math.max(8, left), window.innerWidth - w - 8) + "px";
            el.style.top = Math.min(Math.max(8, top), Math.max(8, window.innerHeight - h - 8)) + "px";
        }

        function pin(c, on) {
            c.pinned = on;
            c.el.classList.toggle("hover-card-pinned", on);
            c.el.dataset.pinned = on ? "true" : "false";
            const b = c.el.querySelector("[data-testid='hover-card-pin']");
            b.setAttribute("aria-pressed", on ? "true" : "false");
            b.setAttribute("aria-label", text(on ? "unpin" : "pin"));
            b.title = text(on ? "unpin" : "pin");
            if (!on) return;
            const pinned = cards.filter(function (x) { return x.pinned; });
            if (pinned.length > MAX_PINNED) close(cards.indexOf(pinned[0]));
        }

        function show(link, src, p, from) {
            trim(from);
            const el = content(p, src);
            const tools = document.createElement("div");
            tools.className = "hover-card-tools";
            const pinButton = button("pin", text("pin"), "hover-card-pin");
            pinButton.setAttribute("aria-pressed", "false");
            const closeButton = button("close", text("close"), "hover-card-close");
            tools.append(pinButton, closeButton);
            el.append(tools);
            el.dataset.pinned = "false";
            const c = { el: el, link: link, src: src, pinned: false };
            pinButton.addEventListener("click", function () { pin(c, !c.pinned); });
            closeButton.addEventListener("click", function () {
                const i = cards.indexOf(c);
                if (i >= 0) { trim(i); close(i); }
                if (link.isConnected) link.focus({ preventScroll: true });
            });
            cards.push(c);
            document.body.appendChild(el);
            link.setAttribute("aria-describedby", el.id);
            place(c, from);
        }

        function start(link) {
            const parent = cards[indexOf(link)] || null;
            // The link of a card already open: keep it, and what it opened.
            if (cards.some(function (c) { return c.link === link; })) { clearTimeout(hideTimer); return; }
            if (pending === link) return;
            cancel();
            const src = source(link);
            if (!src) return;
            pending = link;
            showTimer = setTimeout(function () {
                load(src).then(function (p) {
                    if (!p || pending !== link || !link.isConnected) return;
                    pending = null;
                    // The card the name is on may have closed while this one loaded.
                    const from = parent ? cards.indexOf(parent) : -1;
                    if (parent && from < 0) return;
                    show(link, src, p, from);
                });
            }, SHOW_AFTER);
        }

        document.addEventListener("mouseover", function (e) {
            const t = e.target instanceof Element ? e.target : null;
            if (!t) return;
            const link = t.closest(SELECTOR);
            const i = indexOf(t);
            if (link) { clearTimeout(hideTimer); start(link); return; }
            if (pending) cancel();
            // On card i: the cards it did not open stay a moment, so the pointer can cross to them.
            if (i >= 0) laterTrim(i);
        });
        document.addEventListener("mouseout", function (e) {
            const from = e.target instanceof Element ? e.target : null;
            const to = e.relatedTarget instanceof Element ? e.relatedTarget : null;
            if (!from || (!from.closest(SELECTOR) && indexOf(from) < 0)) return;
            if (to && (to.closest(SELECTOR) || indexOf(to) >= 0)) return;
            cancel();
            laterTrim(-1);
        });
        document.addEventListener("focusin", function (e) {
            const t = e.target instanceof Element ? e.target : null;
            const link = t ? t.closest(SELECTOR) : null;
            if (link) start(link);
            else if (!t || indexOf(t) < 0) { cancel(); trim(-1); }
        });
        // Escape closes the newest card (before the page's own Escape: a phone sheet's, a window's).
        document.addEventListener("keydown", function (e) {
            if (e.key !== "Escape") return;
            cancel();
            if (cards.length === 0) return;
            e.preventDefault();
            const c = cards[cards.length - 1];
            const inside = c.el.contains(document.activeElement);
            close(cards.length - 1);
            if (inside && c.link.isConnected) c.link.focus({ preventScroll: true });
        }, true);
        // Scrolling the page moves the links away from their cards: the cards that are not pinned go. Scrolling in a card does not.
        window.addEventListener("scroll", function (e) {
            if (e.target instanceof Element && indexOf(e.target) >= 0) return;
            cancel();
            trim(-1);
        }, { passive: true, capture: true });
        // A click outside the cards (or on a link, which opens it) closes the cards that are not pinned.
        document.addEventListener("click", function (e) {
            const t = e.target instanceof Element ? e.target : null;
            cancel();
            if (t && indexOf(t) >= 0 && !t.closest("a[href]")) return;
            trim(-1);
        }, true);
        // Leaving the campaign takes its pinned cards with it.
        function left(path) {
            const here = campaignOf(path);
            for (let i = cards.length - 1; i >= 0; i--) if (campaignOf(cards[i].src) !== here) close(i);
        }
        if (window.navigation) window.navigation.addEventListener("navigatesuccess", function () { left(location.pathname); });
        window.addEventListener("popstate", function () { left(location.pathname); });
    }

    // ---- On a touch screen: a tap opens the card in a sheet at the bottom. ----
    function touch() {
        let sheet = null, stack = [], loading = null;

        function close() {
            loading = null;
            stack = [];
            if (sheet) { sheet.remove(); sheet = null; }
        }

        function render() {
            const top = stack[stack.length - 1];
            if (!sheet) {
                sheet = document.createElement("div");
                sheet.className = "preview-sheet-layer";
                sheet.innerHTML = "<div class=\"preview-sheet-backdrop\"></div><div class=\"preview-sheet\" role=\"dialog\" aria-modal=\"true\" data-testid=\"preview-sheet\"></div>";
                sheet.firstChild.addEventListener("click", close);
                document.body.appendChild(sheet);
            }
            const panel = sheet.lastChild;
            const bar = document.createElement("div");
            bar.className = "preview-sheet-bar";
            if (stack.length > 1) {
                const back = button("back", text("back"), "preview-sheet-back");
                back.addEventListener("click", function () { stack.pop(); render(); });
                bar.append(back);
            }
            const open = document.createElement("a");
            open.className = "cx-btn cx-plaque cx-sm preview-sheet-open";
            open.href = top.p.href;
            open.textContent = text("open");
            open.dataset.testid = "preview-sheet-open";
            const shut = button("close", text("close"), "preview-sheet-close");
            shut.addEventListener("click", close);
            bar.append(open, shut);
            const card = content(top.p, top.src);
            panel.setAttribute("aria-labelledby", card.getAttribute("aria-labelledby"));
            panel.replaceChildren(card, bar);
            panel.scrollTop = 0;
        }

        // A tap on a link that has a card opens the card instead; the link itself is followed when there is no card for it.
        document.addEventListener("click", function (e) {
            if (e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
            const t = e.target instanceof Element ? e.target : null;
            const link = t ? t.closest(SELECTOR) : null;
            if (!link) {
                // Open (and the card's own name) go where they say, and close the sheet on the way.
                if (sheet && t && t.closest(".preview-sheet a[href]")) close();
                return;
            }
            const src = source(link);
            if (!src) return;
            e.preventDefault();
            e.stopPropagation();
            const ticket = {};
            loading = ticket;
            load(src).then(function (p) {
                if (loading !== ticket) return;
                loading = null;
                if (!p) {
                    close();
                    if (window.Blazor && window.Blazor.navigateTo) window.Blazor.navigateTo(link.href); else location.href = link.href;
                    return;
                }
                if (!(sheet && sheet.contains(link))) stack = [];
                stack.push({ p: p, src: src });
                render();
            });
        }, true);
        document.addEventListener("keydown", function (e) {
            if (e.key !== "Escape" || !sheet) return;
            e.preventDefault();
            if (stack.length > 1) { stack.pop(); render(); } else close();
        }, true);
        window.addEventListener("popstate", close);
        if (window.navigation) window.navigation.addEventListener("navigatesuccess", close);
    }
})();
