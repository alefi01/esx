/*
 * Код страниц портала.
 *
 * Написан вручную, без jQuery, React и прочего: в вашей сети нет интернета,
 * а разбираться в чужой библиотеке при доработке пришлось бы всё равно.
 *
 * Разметка и приёмы взяты из присланного макета — те же имена классов,
 * те же окна, те же всплывающие сообщения. Отличие одно и оно важное:
 * в макете все данные выдуманы и лежат прямо в коде, а здесь они приходят
 * с сервера, и каждое действие сервер заново проверяет по правам.
 *
 * Файл читается сверху вниз:
 *
 *   1. Мелкие помощники
 *   2. Всплывающие сообщения
 *   3. Окна поверх страницы
 *   4. Контекстное меню
 *   5. Каркас: боковое меню, тема, клавиши, полосы заполнения
 *   6. Колокольчик уведомлений
 *   7. Файлы: выделение, открытие, действия, загрузка
 *   8. Предпросмотр
 */

(function () {
    'use strict';

    // ======================================================================
    // 1. Мелкие помощники
    // ======================================================================

    const $ = (s, root) => (root || document).querySelector(s);
    const $$ = (s, root) => [...(root || document).querySelectorAll(s)];

    /** Собирает элемент из строки разметки — как в макете. */
    const el = h => {
        const t = document.createElement('template');
        t.innerHTML = h.trim();
        return t.content.firstElementChild;
    };

    /**
     * Экранирование текста перед вставкой в разметку.
     *
     * Нужно везде, где в разметку попадает то, что ввёл человек: имя файла,
     * текст сообщения, название папки. Без этого имя вида
     * «<img onerror=...>» превратилось бы в исполняемый код.
     */
    const esc = s => String(s === null || s === undefined ? '' : s)
        .replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

    /** Значок из общего набора — та же запись, что в макете. */
    const ic = (n, s = 18, w = 1.8) =>
        `<svg class="ic" width="${s}" height="${s}" viewBox="0 0 24 24" fill="none" stroke="currentColor" ` +
        `stroke-width="${w}" stroke-linecap="round" stroke-linejoin="round"><use href="#i-${n}"/></svg>`;

    /** Человекочитаемый размер: 12,3 МБ вместо 12897485. */
    const fmtSize = b => {
        if (b < 1024) { return b + ' Б'; }
        if (b < 1048576) { return (b / 1024).toFixed(1).replace('.', ',') + ' КБ'; }
        if (b < 1073741824) { return (b / 1048576).toFixed(1).replace('.', ',') + ' МБ'; }
        return (b / 1073741824).toFixed(2).replace('.', ',') + ' ГБ';
    };

    /** «Сегодня, 14:05» вместо полной даты — так читается быстрее. */
    const relTime = iso => {
        const when = new Date(iso);

        if (isNaN(when.getTime())) { return ''; }

        const minutes = Math.floor((Date.now() - when.getTime()) / 60000);

        if (minutes < 1) { return 'только что'; }
        if (minutes < 60) { return minutes + ' мин назад'; }

        const hours = Math.floor(minutes / 60);

        if (hours < 24) { return hours + ' ч назад'; }
        if (hours < 48) { return 'вчера'; }

        return when.toLocaleDateString('ru-RU');
    };

    /**
     * Токен защиты от подделки запросов. Лежит в скрытом поле любой формы
     * на странице; без него сервер отклонит любой POST.
     */
    function token() {
        const field = $('input[name="__RequestVerificationToken"]');

        return field ? field.value : '';
    }

    /** POST на сервер с токеном. Возвращает обещание с разобранным ответом. */
    function post(url, fields) {
        const body = new FormData();

        body.append('__RequestVerificationToken', token());

        Object.keys(fields || {}).forEach(name => {
            const value = fields[name];

            (Array.isArray(value) ? value : [value]).forEach(v => body.append(name, v));
        });

        return fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'XMLHttpRequest' },
            body
        }).then(response => (response.ok ? response : Promise.reject(response.status)));
    }

    /**
     * Отправка обычной формы: страница перезагрузится, и человек увидит
     * результат, посчитанный сервером. Так работают все изменения, которые
     * должны попасть в журнал и в права.
     */
    function submit(url, fields) {
        const form = document.createElement('form');

        form.method = 'post';
        form.action = url;
        form.hidden = true;

        const add = (name, value) => {
            const input = document.createElement('input');
            input.type = 'hidden';
            input.name = name;
            input.value = value;
            form.appendChild(input);
        };

        add('__RequestVerificationToken', token());

        Object.keys(fields || {}).forEach(name => {
            const value = fields[name];

            (Array.isArray(value) ? value : [value]).forEach(v => add(name, v));
        });

        document.body.appendChild(form);
        form.submit();
    }

    /**
     * Кладёт текст в буфер обмена и говорит об этом человеку.
     *
     * Путей два, и оба нужны. Штатный navigator.clipboard работает только
     * по защищённому соединению (HTTPS), а портал пока живёт по HTTP —
     * поэтому есть запасной: невидимое поле ввода и старая команда copy.
     * Она устарела, но работает везде и без всяких условий.
     */
    function copyText(text, okMessage) {
        const fallback = () => {
            const field = document.createElement('textarea');

            field.value = text;
            field.setAttribute('readonly', '');
            field.style.position = 'fixed';
            field.style.opacity = '0';

            document.body.appendChild(field);
            field.select();

            let ok = false;

            try { ok = document.execCommand('copy'); } catch (error) { ok = false; }

            field.remove();

            return ok;
        };

        const done = ok => toast(
            ok ? (okMessage || 'Скопировано') : 'Не удалось скопировать — возьмите адрес из строки браузера',
            ok ? 'ok' : 'warn');

        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(text).then(() => done(true)).catch(() => done(fallback()));
        } else {
            done(fallback());
        }
    }

    // ======================================================================
    // 2. Всплывающие сообщения
    //
    // Появляются справа внизу и сами исчезают. Нужны для короткого отклика
    // на действие: «скопировано», «не удалось». Для важного и необратимого
    // есть окно с подтверждением — см. дальше.
    // ======================================================================

    const TOAST_COLOR = { info: 'var(--accent)', ok: 'var(--ok)', warn: 'var(--warn)', danger: 'var(--danger)' };
    const TOAST_ICON = { info: 'info', ok: 'check', warn: 'alert', danger: 'trash' };

    function toast(msg, type, actionLabel, actionFn) {
        const kind = type || 'info';
        const root = $('#toastRoot');

        if (!root) { return null; }

        const t = el(
            `<div class="toast"><div class="t-ic t-ic--${kind}">${ic(TOAST_ICON[kind], 16, 2)}</div>` +
            `<p>${esc(msg)}</p>` +
            (actionLabel ? `<button class="t-act" type="button">${esc(actionLabel)}</button>` : '') +
            `<button class="t-x" type="button" aria-label="Закрыть">${ic('x', 14)}</button></div>`);

        root.appendChild(t);

        const kill = () => {
            t.classList.add('out');
            setTimeout(() => t.remove(), 280);
        };

        t.querySelector('.t-x').onclick = kill;

        if (actionLabel) {
            t.querySelector('.t-act').onclick = () => {
                if (actionFn) { actionFn(); }
                kill();
            };
        }

        setTimeout(kill, actionLabel ? 6000 : 4200);

        return t;
    }

    // Наружу — чтобы сервер мог показать сообщение после перезагрузки
    // страницы через data-атрибут (см. раздел 5).
    window.portalToast = toast;

    // ======================================================================
    // 3. Окна поверх страницы
    // ======================================================================

    function openModal(options) {
        closeModal();

        const wide = options.wide ? ' wide' : '';

        const overlay = el(
            `<div class="overlay"><div class="modal${wide}">` +
            `<div class="modal-head"><h3>${options.title}</h3>` +
            `<button class="icon-btn" type="button" data-mclose aria-label="Закрыть">${ic('x', 17)}</button></div>` +
            `<div class="modal-body${options.wide ? ' modal-body--wide' : ''}">${options.body}</div>` +
            (options.foot ? `<div class="modal-foot">${options.foot}</div>` : '') +
            `</div></div>`);

        $('#modalRoot').appendChild(overlay);

        // Нажатие мимо окна закрывает его. Именно mousedown, а не click:
        // иначе окно закроется, если начать выделять текст внутри него
        // и отпустить кнопку мыши снаружи.
        overlay.addEventListener('mousedown', e => {
            if (e.target === overlay) { closeModal(); }
        });

        $$('[data-mclose]', overlay).forEach(b => { b.onclick = closeModal; });

        return overlay;
    }

    function closeModal() {
        const m = $('#modalRoot .overlay');

        if (m) { m.remove(); }
    }

    /**
     * Окно подтверждения вместо системного confirm().
     * Системное выглядит по-разному в каждом браузере и выбивается
     * из оформления; здесь и вид единый, и текст можно написать нормальный.
     */
    function confirmDlg(title, text, okLabel, onOk, danger) {
        const m = openModal({
            title: esc(title),
            body: `<p class="dlg-text">${esc(text)}</p>`,
            foot: `<button class="btn" type="button" data-mclose>Отмена</button>` +
                `<button class="btn ${danger ? 'danger' : 'primary'}" type="button" id="cOk">${esc(okLabel)}</button>`
        });

        $$('[data-mclose]', m).forEach(b => { b.onclick = closeModal; });

        $('#cOk', m).onclick = () => { closeModal(); onOk(); };
    }

    /** Окно с одним полем ввода: создать папку, переименовать. */
    function promptDlg(title, label, value, okLabel, onOk) {
        const m = openModal({
            title: esc(title),
            body: `<div class="field"><label for="pIn">${esc(label)}</label>` +
                `<input type="text" id="pIn" maxlength="255" value="${esc(value)}"></div>`,
            foot: `<button class="btn" type="button" data-mclose>Отмена</button>` +
                `<button class="btn primary" type="button" id="pOk">${esc(okLabel)}</button>`
        });

        const input = $('#pIn', m);

        setTimeout(() => { input.focus(); input.select(); }, 60);

        const go = () => {
            const v = input.value.trim();

            if (v) { closeModal(); onOk(v); }
        };

        $('#pOk', m).onclick = go;
        input.onkeydown = e => { if (e.key === 'Enter') { go(); } };
    }

    // ======================================================================
    // 4. Контекстное меню (правая кнопка мыши)
    // ======================================================================

    let ctxEl = null;

    function showMenu(x, y, entries) {
        hideMenu();

        ctxEl = el(`<div class="ctx">${entries.map((e, i) => e.sep
            ? '<div class="sep"></div>'
            : `<button type="button" data-i="${i}"${e.danger ? ' class="danger"' : ''}>${ic(e.icon, 16)}${esc(e.label)}</button>`
        ).join('')}</div>`);

        document.body.appendChild(ctxEl);

        // Меню не должно вылезать за край экрана: если места справа
        // или снизу не хватает, сдвигаем его внутрь.
        const r = ctxEl.getBoundingClientRect();

        ctxEl.style.left = Math.min(x, window.innerWidth - r.width - 10) + 'px';
        ctxEl.style.top = Math.min(y, window.innerHeight - r.height - 10) + 'px';

        entries.forEach((e, i) => {
            if (e.sep) { return; }

            $(`[data-i="${i}"]`, ctxEl).onclick = () => { hideMenu(); e.fn(); };
        });
    }

    function hideMenu() {
        if (ctxEl) { ctxEl.remove(); ctxEl = null; }
    }

    document.addEventListener('click', e => { if (!e.target.closest('.ctx')) { hideMenu(); } });
    document.addEventListener('scroll', hideMenu, true);

    // ======================================================================
    // 5. Каркас: боковое меню, тема, клавиши, полосы заполнения
    // ======================================================================

    // Полосы заполнения. Ширину задаёт код, а не разметка: политика
    // безопасности страницы запрещает встроенные стили, и атрибут
    // style="width:40%" браузер молча отбросит.
    $$('[data-percent]').forEach(bar => {
        const percent = parseFloat(bar.dataset.percent);

        setTimeout(() => {
            bar.style.width = (isNaN(percent) ? 0 : Math.min(100, Math.max(0, percent))) + '%';
        }, 150);
    });

    // Боковое меню на узком экране.
    (function () {
        const burger = $('#burgerBtn');
        const sidebar = $('#sidebar');

        if (!burger || !sidebar) { return; }

        burger.onclick = () => sidebar.classList.toggle('open');

        // Нажали пункт меню — оно закрывается: после перехода на другую
        // страницу открытое меню только мешает.
        sidebar.addEventListener('click', e => {
            if (e.target.closest('.nav-item')) { sidebar.classList.remove('open'); }
        });
    })();

    // Переключатель темы.
    (function () {
        const button = $('#themeBtn');
        const iconUse = $('#themeIconUse');

        if (!button || !window.portalTheme) { return; }

        const paint = mode => iconUse.setAttribute('href', mode === 'dark' ? '#i-sun' : '#i-moon');

        paint(window.portalTheme.current());

        button.onclick = () => paint(window.portalTheme.toggle());
    })();

    // Сообщение, оставленное сервером после перезагрузки страницы.
    (function () {
        const holder = $('[data-flash]');

        if (!holder) { return; }

        const text = holder.dataset.flash;

        if (text) { toast(text, holder.dataset.flashKind || 'ok'); }
    })();

    /**
     * Подтверждение перед необратимым действием.
     *
     * Достаточно повесить на кнопку отправки формы data-confirm с текстом
     * вопроса. Нужно там, где отменить уже нельзя: очистка корзины,
     * стирание файла, очистка журнала.
     */
    document.addEventListener('click', e => {
        const button = e.target.closest('[data-confirm]');

        if (!button || button.dataset.confirmed === '1') { return; }

        e.preventDefault();

        confirmDlg(
            button.dataset.confirmTitle || 'Подтвердите действие',
            button.dataset.confirm,
            button.dataset.confirmOk || 'Выполнить',
            () => {
                // Второй раз спрашивать не надо: помечаем кнопку
                // и нажимаем её заново, теперь уже по-настоящему.
                button.dataset.confirmed = '1';
                button.click();
            },
            true);
    }, true);

    // Клавиши — те же, что в макете.
    document.addEventListener('keydown', e => {
        // Ctrl+K переводит курсор в поиск, где бы человек ни находился.
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
            const search = $('#globalSearch');

            if (search) { e.preventDefault(); search.focus(); search.select(); }

            return;
        }

        if (e.key === 'Escape') {
            hideMenu();
            closeModal();

            const panel = $('#notifPanel');

            if (panel) { panel.hidden = true; }

            const sidebar = $('#sidebar');

            if (sidebar) { sidebar.classList.remove('open'); }
        }
    });

    // ======================================================================
    // 6. Колокольчик уведомлений
    //
    // Раз в минуту спрашиваем сервер, нет ли нового. Постоянного соединения
    // нет намеренно: между офисами канал с урезанным MTU, и рвущийся
    // WebSocket выглядел бы как «портал завис». Один короткий запрос
    // в минуту на двадцать человек — это ничто.
    // ======================================================================

    (function () {
        const bell = $('#bellBtn');
        const badge = $('#bellBadge');
        const panel = $('#notifPanel');

        if (!bell || !panel) { return; }

        const POLL_MS = 60000;

        // Что уже показывали всплывающим сообщением — чтобы не показывать
        // одно и то же каждую минуту. Живёт до перезагрузки страницы.
        const announced = new Set();
        let first = true;
        let timer = null;
        let toldAboutSignOut = false;

        const NOTIF_ICON = { announcement: 'mega', message: 'chat' };

        function setCount(id, value) {
            const element = $(id);

            if (!element) { return; }

            element.hidden = !value;
            element.textContent = value > 99 ? '99+' : String(value);
        }

        function render(data) {
            const unread = data.unread || 0;

            badge.hidden = unread === 0;
            badge.textContent = unread > 99 ? '99+' : String(unread);

            setCount('#chatBadge', data.messages || 0);
            setCount('#annBadge', data.announcements || 0);

            const items = data.items || [];

            panel.innerHTML =
                '<div class="notif-head"><b>Уведомления</b>' +
                '<button type="button" id="readAllBtn">Прочитать все</button></div>' +
                '<div class="notif-scroll">' +
                (items.length
                    ? items.map(n => {
                        const kind = n.kind === 'message' ? 'message' : 'announcement';

                        // Ссылка, а не кнопка: нажатие ведёт к самому событию —
                        // к объявлению или к переписке. Всё непрочитанное
                        // при этом отмечается прочитанным, иначе счётчик
                        // на колокольчике висел бы до отдельного нажатия.
                        return `<a class="notif" href="${esc(n.url)}" data-seen>` +
                            `<div class="n-ic n-ic--${kind}">${ic(NOTIF_ICON[kind], 17)}</div>` +
                            `<p><b>${esc(n.title)}</b>` +
                            `<time>${esc(n.author)} · ${esc(relTime(n.at))}</time></p>` +
                            '<div class="ndot"></div></a>';
                    }).join('')
                    : '<div class="empty notif-empty"><p>Нет уведомлений</p></div>') +
                '</div>';

            // Переход по уведомлению = «я это видел».
            $$('[data-seen]', panel).forEach(link => {
                link.addEventListener('click', () => {
                    // Ждать ответа не нужно: человек уже уходит на страницу,
                    // а отметка успеет дойти до сервера сама.
                    post('/api/notifications/seen', {}).catch(() => { /* не страшно */ });
                });
            });

            $('#readAllBtn', panel).onclick = e => {
                e.stopPropagation();

                post('/api/notifications/seen', {})
                    .then(() => { panel.hidden = true; poll(); })
                    .catch(() => toast('Не удалось отметить прочитанным', 'warn'));
            };

            // При первой проверке всплывающих сообщений не показываем:
            // человек только что открыл страницу, и непрочитанное для него
            // не новость, а просто счётчик на колокольчике.
            if (first) {
                items.forEach(n => announced.add(n.kind + ':' + n.id));
                first = false;
                return;
            }

            items.forEach(n => {
                const key = n.kind + ':' + n.id;

                if (announced.has(key)) { return; }

                announced.add(key);

                toast((n.kind === 'message' ? 'Новое сообщение: ' : 'Новое объявление: ') + n.title, 'info');
            });
        }

        function poll() {
            fetch('/api/notifications', {
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
                .then(render)
                .catch(reason => {
                    // Истёкший вход — не «связь моргнула». Молчать про него
                    // нельзя: страница выглядит рабочей, а на деле не работает
                    // уже ничего. Говорим один раз и перестаём спрашивать.
                    if (reason === 401) {
                        if (timer !== null) { clearInterval(timer); timer = null; }

                        badge.hidden = true;

                        if (!toldAboutSignOut) {
                            toldAboutSignOut = true;
                            toast('Вход в портал истёк. Обновите страницу и войдите заново.', 'danger');
                        }
                    }

                    // Всё остальное — молча: связь могла моргнуть, а ругаться
                    // на это раз в минуту было бы издевательством.
                });
        }

        bell.onclick = e => {
            e.stopPropagation();
            panel.hidden = !panel.hidden;
        };

        document.addEventListener('click', e => {
            if (!panel.hidden && !e.target.closest('.notif-wrap')) { panel.hidden = true; }
        });

        poll();
        timer = setInterval(poll, POLL_MS);

        // Вернулись на вкладку — проверяем сразу, не дожидаясь минуты.
        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState === 'visible' && timer !== null) { poll(); }
        });
    })();

    // ======================================================================
    // 7. Файлы
    // ======================================================================

    const files = $('#filesArea');

    if (files) {
        const container = $('#filesContainer');
        const settings = files.dataset;

        const folderId = settings.folderId || '';
        const canWrite = settings.canWrite === 'true';
        const canCreateFolder = settings.canCreateFolder === 'true';

        let selected = null;

        /** Данные выделенного объекта из его разметки. */
        const info = node => ({
            id: node.dataset.id,
            file: node.dataset.file || '',
            folder: node.dataset.folder || '',
            name: node.dataset.name || '',
            href: node.dataset.href || '',
            kind: node.dataset.kind || '',
            fav: node.dataset.fav === 'true',
            canDelete: node.dataset.canDelete === 'true',
            canManage: node.dataset.canManage === 'true'
        });

        function select(node) {
            $$('.selected', container).forEach(x => x.classList.remove('selected'));

            selected = node || null;

            if (node) { node.classList.add('selected'); }
        }

        // Одиночное нажатие выделяет, двойное открывает — как в проводнике.
        container.addEventListener('click', e => {
            if (e.target.closest('.fav-star')) { return; }

            select(e.target.closest('[data-id]'));
        });

        container.addEventListener('dblclick', e => {
            const node = e.target.closest('[data-id]');

            if (node) { open(info(node), node); }
        });

        container.addEventListener('contextmenu', e => {
            const node = e.target.closest('[data-id]');

            e.preventDefault();

            if (node) {
                select(node);
                itemMenu(info(node), e.clientX, e.clientY);
            } else {
                areaMenu(e.clientX, e.clientY);
            }
        });

        function open(item, node) {
            if (item.folder) {
                window.location.href = item.href;
                return;
            }

            // Показать умеем не всё. Что не умеем — просто скачиваем:
            // «открыть» для такого файла и есть «скачать».
            if (item.kind) {
                openPreview(item, node);
            } else {
                window.location.href = item.href;
            }
        }

        function itemMenu(item, x, y) {
            const entries = [];

            entries.push(item.folder
                ? { icon: 'folder', label: 'Открыть', fn: () => open(item, null) }
                : { icon: 'eye', label: 'Предпросмотр', fn: () => open(item, $(`[data-id="${item.id}"]`, container)) });

            if (item.file) {
                entries.push({ icon: 'download', label: 'Скачать', fn: () => { window.location.href = item.href; } });
            }

            entries.push({
                icon: 'star',
                label: item.fav ? 'Убрать из избранного' : 'В избранное',
                fn: () => toggleFav(item)
            });

            entries.push({ icon: 'share', label: 'Скопировать ссылку', fn: () => copyLink(item) });

            if (item.file && item.canDelete) {
                entries.push({ icon: 'edit', label: 'Переименовать', fn: () => rename(item) });
            }

            if (item.folder && item.canManage) {
                entries.push({ icon: 'edit', label: 'Переименовать', fn: () => rename(item) });
                entries.push({ icon: 'info', label: 'Управление папкой', fn: () => { window.location.href = '/Files/Settings?id=' + item.folder; } });
            }

            entries.push({ icon: 'info', label: 'Свойства', fn: () => properties(item) });

            if ((item.file && item.canDelete) || (item.folder && item.canManage)) {
                entries.push({ sep: 1 });
                entries.push({ icon: 'trash', label: 'Удалить', danger: 1, fn: () => remove(item) });
            }

            showMenu(x, y, entries);
        }

        function areaMenu(x, y) {
            const entries = [];

            if (canCreateFolder) {
                entries.push({ icon: 'plus', label: 'Создать папку', fn: newFolder });
            }

            if (canWrite) {
                entries.push({ icon: 'upload', label: 'Загрузить файлы', fn: () => $('#fileInput').click() });
            }

            entries.push({ sep: 1 });
            entries.push({ icon: 'restore', label: 'Обновить', fn: () => window.location.reload() });

            showMenu(x, y, entries);
        }

        function toggleFav(item) {
            const query = item.file ? 'fileId=' + item.file : 'folderId=' + item.folder;

            post('/Files?handler=Favorite&' + query, {})
                .then(r => r.json())
                .then(data => {
                    const node = $(`[data-id="${item.id}"]`, container);

                    if (node) { node.dataset.fav = String(data.favorite); }

                    const star = node && $('.fav-star', node);

                    if (star) { star.classList.toggle('on', data.favorite); }

                    toast(data.favorite ? 'Добавлено в избранное' : 'Убрано из избранного', 'info');
                })
                .catch(() => toast('Не удалось изменить избранное', 'danger'));
        }

        function copyLink(item) {
            // Ссылка ОБЫЧНАЯ, не «публичная»: анонимного доступа портал
            // не даёт вовсе. Тот, кому её пошлют, откроет файл или папку,
            // только если у него и так есть к ней доступ.
            copyText(
                location.origin + (item.folder ? '/Files?id=' + item.folder : item.href),
                item.folder ? 'Ссылка на папку скопирована' : 'Ссылка на файл скопирована');
        }

        function rename(item) {
            promptDlg('Переименовать', 'Новое название', item.name, 'Сохранить', value => {
                submit(item.file
                    ? '/Files?handler=RenameFile'
                    : '/Files?handler=RenameFolder',
                    item.file
                        ? { fileId: item.file, newName: value }
                        : { folderId: item.folder, newName: value });
            });
        }

        function newFolder() {
            promptDlg('Создать папку', 'Название папки', 'Новая папка', 'Создать', value => {
                const url = folderId
                    ? '/Files?handler=CreateFolder&parentId=' + folderId
                    : '/Files?handler=CreateFolder';

                submit(url, { NewFolderName: value });
            });
        }

        function remove(item) {
            if (item.folder) {
                confirmDlg('Удалить папку?',
                    `Папка «${item.name}» будет удалена. Удалить можно только пустую папку.`,
                    'Удалить',
                    () => submit('/Files?handler=DeleteFolder', { folderId: item.folder }),
                    true);

                return;
            }

            confirmDlg('Переместить в корзину?',
                `Файл «${item.name}» отправится в корзину. Восстановить его можно будет из раздела «Корзина».`,
                'В корзину',
                () => submit('/Files?handler=DeleteFiles' + (folderId ? '&folderId=' + folderId : ''),
                    { fileIds: item.file }),
                true);
        }

        function properties(item) {
            const query = item.file ? 'fileId=' + item.file : 'folderId=' + item.folder;

            fetch('/Files?handler=Properties&' + query, {
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
                .then(data => openModal({
                    title: esc(data.title),
                    body: '<div class="pv-meta"><h4>Сведения</h4>' +
                        (data.rows || []).map(row =>
                            `<div class="row"><span>${esc(row.name)}</span><b>${esc(row.value)}</b></div>`).join('') +
                        '</div>',
                    foot: '<button class="btn" type="button" data-mclose>Закрыть</button>'
                }))
                .catch(() => toast('Не удалось получить сведения', 'warn'));
        }

        // ---------- Клавиши в списке файлов ----------
        document.addEventListener('keydown', e => {
            if (!selected || /INPUT|TEXTAREA|SELECT/.test(e.target.tagName) || $('#modalRoot .overlay')) {
                return;
            }

            const item = info(selected);

            if (e.key === 'Delete' && ((item.file && item.canDelete) || (item.folder && item.canManage))) {
                e.preventDefault();
                remove(item);
            }

            if (e.key === 'F2' && ((item.file && item.canDelete) || (item.folder && item.canManage))) {
                e.preventDefault();
                rename(item);
            }

            if (e.key === 'Enter') {
                e.preventDefault();
                open(item, selected);
            }
        });

        // ---------- Звёздочка «в избранном» ----------
        container.addEventListener('click', e => {
            const star = e.target.closest('.fav-star');

            if (!star) { return; }

            e.preventDefault();
            e.stopPropagation();

            toggleFav(info(star.closest('[data-id]')));
        });

        // ---------- Кнопки верхней панели ----------
        const newFolderBtn = $('#newFolderBtn');
        const uploadBtn = $('#uploadBtn');
        const fileInput = $('#fileInput');

        if (newFolderBtn) { newFolderBtn.onclick = newFolder; }
        if (uploadBtn && fileInput) { uploadBtn.onclick = () => fileInput.click(); }
        if (fileInput) { fileInput.onchange = e => upload(e.target.files); }

        // Порядок сортировки и вид списка.
        const sortSel = $('#sortSel');

        if (sortSel) { sortSel.onchange = () => sortSel.form.submit(); }

        (function () {
            const gridBtn = $('#gridBtn');
            const listBtn = $('#listBtn');

            if (!gridBtn || !listBtn) { return; }

            const KEY = 'portal.fileView';

            // Вид — дело вкуса конкретного человека, сервер про него
            // не знает: выбор хранится в браузере и действует на всех
            // страницах портала.
            const read = () => {
                try { return localStorage.getItem(KEY); } catch (error) { return null; }
            };

            const remember = mode => {
                try { localStorage.setItem(KEY, mode); } catch (error) { /* пусть будет так */ }
            };

            const apply = mode => {
                const list = mode === 'list';

                $$('.fgrid', container).forEach(x => { x.hidden = list; });
                $$('.flist', container).forEach(x => { x.hidden = !list; });

                gridBtn.classList.toggle('on', !list);
                listBtn.classList.toggle('on', list);
            };

            apply(read() === 'list' ? 'list' : 'grid');

            gridBtn.onclick = () => { apply('grid'); remember('grid'); };
            listBtn.onclick = () => { apply('list'); remember('list'); };
        })();

        // ---------- Загрузка файлов ----------
        function upload(list) {
            if (!list || !list.length || !canWrite) { return; }

            const data = new FormData();

            data.append('__RequestVerificationToken', token());

            [...list].forEach(f => data.append('uploads', f, f.name));

            const box = el('<div class="uploader"><b>Загрузка: ' + list.length + ' файл(ов)…</b>' +
                '<div class="bar"><i></i></div><p>0%</p></div>');

            document.body.appendChild(box);

            const bar = $('i', box);
            const percent = $('p', box);

            // XMLHttpRequest, а не fetch: только он умеет сообщать, сколько
            // уже отправлено. На канале между офисами файл может идти
            // долго, и без полосы человек решит, что портал завис.
            const request = new XMLHttpRequest();

            request.open('POST', '/Files?handler=Upload&folderId=' + folderId);
            request.setRequestHeader('X-Requested-With', 'XMLHttpRequest');

            request.upload.onprogress = e => {
                if (!e.lengthComputable) { return; }

                const value = Math.round(e.loaded / e.total * 100);

                bar.style.width = value + '%';
                percent.textContent = value + '%';
            };

            request.onload = () => {
                box.remove();

                // Сервер отвечает перенаправлением на ту же папку —
                // перезагружаем страницу, чтобы увидеть загруженное
                // вместе со всеми проверками, которые сервер уже сделал.
                window.location.reload();
            };

            request.onerror = () => {
                box.remove();
                toast('Не удалось загрузить файлы: связь прервалась', 'danger');
            };

            request.send(data);
        }

        // Перетаскивание файлов из проводника.
        if (canWrite) {
            let depth = 0;

            files.addEventListener('dragenter', e => {
                e.preventDefault();
                depth++;
                files.classList.add('dragging');
            });

            files.addEventListener('dragover', e => e.preventDefault());

            files.addEventListener('dragleave', () => {
                depth = Math.max(0, depth - 1);

                if (depth === 0) { files.classList.remove('dragging'); }
            });

            files.addEventListener('drop', e => {
                e.preventDefault();
                depth = 0;
                files.classList.remove('dragging');

                if (e.dataTransfer && e.dataTransfer.files.length) { upload(e.dataTransfer.files); }
            });

            // Ctrl+V на странице папки: вставка файла из буфера обмена Windows.
            document.addEventListener('paste', e => {
                if (/INPUT|TEXTAREA/.test(e.target.tagName)) { return; }

                const items = e.clipboardData && e.clipboardData.files;

                if (items && items.length) { upload(items); }
            });
        }
    }

    // ======================================================================
    // 8. Предпросмотр
    //
    // Окно поверх страницы: слева содержимое файла, справа колонка сведений.
    // Что именно показать, решает сервер — он же отдаёт только то, что есть
    // в белом списке (см. PreviewSupport.cs).
    // ======================================================================


    // ======================================================================
    // 9. Переписки
    //
    // Страница обычная, серверная: список бесед и сообщения приходят готовыми.
    // Здесь только то, ради чего нужен код на странице:
    //   * отправка по Enter и растущее поле ввода;
    //   * выбор собеседников (окно с поиском по справочнику);
    //   * управление группой;
    //   * проверка, не написал ли кто-нибудь, пока страница открыта.
    // ======================================================================

    const chat = $('[data-chat]');

    if (chat) {
        const settings = chat.dataset;
        const conversationId = settings.conversation || '';

        // ---------- Лента сообщений ----------
        const msgs = $('#msgs');

        // Прокручиваем к последнему: человек открывает переписку, чтобы
        // увидеть свежее, а не то, что писали полгода назад.
        if (msgs) { msgs.scrollTop = msgs.scrollHeight; }

        // ---------- Поле ввода ----------
        const form = $('#chatForm');
        const text = $('#chatText');
        const files = $('#chatFiles');
        const attached = $('#chatAttached');

        if (text) {
            // Поле растёт вместе с текстом, но не выше предела из стилей.
            const grow = () => {
                text.style.height = 'auto';
                text.style.height = Math.min(text.scrollHeight, 110) + 'px';
            };

            text.addEventListener('input', grow);
            grow();

            text.addEventListener('keydown', e => {
                // Enter отправляет, Shift+Enter переводит строку — так же,
                // как в любом мессенджере. Иначе придётся тянуться к мыши
                // после каждой реплики.
                if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault();

                    if (text.value.trim() || (files && files.files.length)) {
                        form.requestSubmit();
                    }
                }
            });
        }

        const attachBtn = $('#attachBtn');

        if (attachBtn && files) {
            attachBtn.onclick = () => files.click();

            files.onchange = () => {
                if (!attached) { return; }

                attached.hidden = files.files.length === 0;

                attached.innerHTML = [...files.files]
                    .map(f => `<span class="chip">${esc(f.name)} · ${fmtSize(f.size)}</span>`)
                    .join('');
            };
        }

        // ---------- Удаление сообщения ----------
        chat.addEventListener('click', e => {
            const button = e.target.closest('[data-delete-message]');

            if (!button) { return; }

            confirmDlg('Удалить сообщение?',
                'Сообщение исчезнет у всех участников переписки. Отменить это нельзя.',
                'Удалить',
                () => submit('/Messages?handler=DeleteMessage&id=' + conversationId,
                    { messageId: button.dataset.deleteMessage }),
                true);
        });

        // ---------- Выбор людей ----------
        //
        // Один и тот же список сотрудников нужен в трёх местах: «написать»,
        // «создать группу», «добавить в группу». Поэтому он собран одной
        // функцией, а различается только тем, что делать с выбранным.

        /**
         * Окно со строкой поиска и списком сотрудников.
         *
         * multi = false — нажатие сразу выполняет действие и закрывает окно;
         * multi = true  — выбранные накапливаются, действие по кнопке внизу.
         */
        function peoplePicker(options) {
            const chosen = new Map();

            const m = openModal({
                title: esc(options.title),
                body: (options.extra || '') +
                    '<div class="field"><label for="peopleSearch">' + esc(options.label) + '</label>' +
                    '<input type="text" id="peopleSearch" autocomplete="off" ' +
                    'placeholder="Начните вводить фамилию или логин"></div>' +
                    '<div class="chosen" id="peopleChosen" hidden></div>' +
                    '<div class="people" id="peopleList"><p class="hint">Загружается…</p></div>',
                foot: options.multi
                    ? '<button class="btn" type="button" data-mclose>Отмена</button>' +
                      '<button class="btn primary" type="button" id="peopleOk">' + esc(options.okLabel) + '</button>'
                    : '<button class="btn" type="button" data-mclose>Отмена</button>'
            });

            const search = $('#peopleSearch', m);
            const list = $('#peopleList', m);
            const chosenBox = $('#peopleChosen', m);

            const paintChosen = () => {
                chosenBox.hidden = chosen.size === 0;

                chosenBox.innerHTML = [...chosen.values()]
                    .map(p => `<span class="chip">${esc(p.displayName)}</span>`).join('');
            };

            const load = () => {
                fetch('/Messages?handler=People&q=' + encodeURIComponent(search.value.trim()), {
                    credentials: 'same-origin',
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                })
                    .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
                    .then(people => {
                        if (people.length === 0) {
                            list.innerHTML = '<p class="hint">Никого не нашлось. ' +
                                'Справочник сотрудников читается из Active Directory.</p>';
                            return;
                        }

                        list.innerHTML = people.map(p =>
                            `<button class="member-row" type="button" data-login="${esc(p.userName)}" ` +
                            `data-name="${esc(p.displayName)}">` +
                            `<div class="avatar" data-av="${avatarIndex(p.displayName)}">${esc(initials(p.displayName))}</div>` +
                            `<div><b>${esc(p.displayName)}</b><span>${esc(p.userName)}</span></div>` +
                            (options.multi ? '<span class="member-mark"></span>' : '') +
                            '</button>').join('');

                        $$('.member-row', list).forEach(row => {
                            const login = row.dataset.login;

                            if (chosen.has(login)) { row.classList.add('on'); }

                            row.onclick = () => {
                                if (!options.multi) {
                                    closeModal();
                                    options.onPick(login, row.dataset.name);
                                    return;
                                }

                                if (chosen.has(login)) {
                                    chosen.delete(login);
                                    row.classList.remove('on');
                                } else {
                                    chosen.set(login, { userName: login, displayName: row.dataset.name });
                                    row.classList.add('on');
                                }

                                paintChosen();
                            };
                        });
                    })
                    .catch(() => {
                        list.innerHTML = '<p class="hint">Не удалось получить список сотрудников. ' +
                            'Возможно, недоступен контроллер домена.</p>';
                    });
            };

            // Ждём, пока человек допечатает: запрос уходит в Active Directory,
            // и дёргать его на каждую букву незачем.
            let timer = null;

            search.addEventListener('input', () => {
                clearTimeout(timer);
                timer = setTimeout(load, 250);
            });

            setTimeout(() => search.focus(), 60);
            load();

            if (options.multi) {
                $('#peopleOk', m).onclick = () => {
                    if (chosen.size === 0) {
                        toast('Никто не выбран', 'warn');
                        return;
                    }

                    closeModal();
                    options.onDone([...chosen.keys()], m);
                };
            }

            return m;
        }

        /** Те же буквы и цвет кружка, что считает сервер (см. Avatars). */
        function initials(name) {
            const parts = String(name || '').split(/[\s._-]+/).filter(Boolean);

            if (parts.length === 0) { return '?'; }

            return (parts.length === 1 ? parts[0][0] : parts[0][0] + parts[1][0]).toUpperCase();
        }

        function avatarIndex(name) {
            let sum = 0;

            for (const c of String(name || '')) {
                sum = (sum * 31 + c.charCodeAt(0)) & 0x7fffffff;
            }

            return sum % 8;
        }

        // ---------- Кнопки ----------
        const newDirect = $('#newDirectBtn');

        if (newDirect) {
            newDirect.onclick = () => peoplePicker({
                title: 'Написать сотруднику',
                label: 'Кому',
                onPick: login => submit('/Messages?handler=Start', { withUserName: login })
            });
        }

        const newGroup = $('#newGroupBtn');

        if (newGroup) {
            newGroup.onclick = () => {
                const m = peoplePicker({
                    title: 'Создать группу',
                    label: 'Кого добавить',
                    multi: true,
                    okLabel: 'Создать',
                    extra: '<div class="field"><label for="groupTitle">Название группы</label>' +
                        '<input type="text" id="groupTitle" maxlength="120" placeholder="Например: Отдел кадров"></div>',
                    onDone: logins => {
                        const title = ($('#groupTitle', m) || {}).value || '';

                        submit('/Messages?handler=CreateGroup', {
                            GroupTitle: title.trim() || 'Новая группа',
                            Members: logins
                        });
                    }
                });
            };
        }

        const groupBtn = $('#groupBtn');

        if (groupBtn) {
            groupBtn.onclick = () => openGroupWindow();
        }

        /** Окно «Участники группы»: список, переименование, выход. */
        function openGroupWindow() {
            const isOwner = settings.isOwner === 'true';

            // Список участников уже есть на странице — берём его оттуда,
            // а не ходим на сервер второй раз за тем же самым.
            const rows = $$('[data-participant]').map(node => ({
                login: node.dataset.participant,
                name: node.dataset.name,
                owner: node.dataset.owner === 'true'
            }));

            const m = openModal({
                title: 'Участники группы',
                body: (isOwner
                    ? '<div class="field"><label for="groupNewTitle">Название группы</label>' +
                      '<div class="inline-pair"><input type="text" id="groupNewTitle" maxlength="120" ' +
                      `value="${esc(settings.title || '')}">` +
                      '<button class="btn" type="button" id="groupRename">Сохранить</button></div></div>'
                    : '') +
                    '<div class="people">' + rows.map(p =>
                        '<div class="member-row member-row--static">' +
                        `<div class="avatar" data-av="${avatarIndex(p.name)}">${esc(initials(p.name))}</div>` +
                        `<div><b>${esc(p.name)}</b><span>${p.owner ? 'создатель' : 'участник'}</span></div>` +
                        (isOwner && p.login !== settings.me
                            ? `<button class="link danger" type="button" data-drop="${esc(p.login)}">убрать</button>`
                            : '') +
                        '</div>').join('') + '</div>',
                foot: (isOwner ? '<button class="btn" type="button" id="groupAdd">Добавить человека</button>' : '') +
                    '<button class="btn danger" type="button" id="groupLeave">Выйти из группы</button>' +
                    '<button class="btn" type="button" data-mclose>Закрыть</button>'
            });

            const rename = $('#groupRename', m);

            if (rename) {
                rename.onclick = () => submit('/Messages?handler=RenameGroup&id=' + conversationId,
                    { newTitle: $('#groupNewTitle', m).value });
            }

            $$('[data-drop]', m).forEach(b => {
                b.onclick = () => submit('/Messages?handler=RemoveMember&id=' + conversationId,
                    { memberUserName: b.dataset.drop });
            });

            const add = $('#groupAdd', m);

            if (add) {
                add.onclick = () => peoplePicker({
                    title: 'Добавить в группу',
                    label: 'Кого добавить',
                    onPick: login => submit('/Messages?handler=AddMember&id=' + conversationId,
                        { memberUserName: login })
                });
            }

            $('#groupLeave', m).onclick = () => {
                closeModal();

                confirmDlg('Выйти из группы?',
                    'Вы перестанете получать сообщения этой группы. Вернуться можно, только если вас добавят заново.',
                    'Выйти',
                    () => submit('/Messages?handler=RemoveMember&id=' + conversationId,
                        { memberUserName: settings.me }),
                    true);
            };
        }

        // ---------- Не написал ли кто-нибудь, пока страница открыта ----------
        if (conversationId) {
            const CHECK_MS = 8000;

            let lastId = parseInt(settings.lastMessage, 10) || 0;
            let told = false;

            setInterval(() => {
                if (document.visibilityState !== 'visible' || told) { return; }

                fetch(`/Messages?handler=New&id=${conversationId}&afterId=${lastId}`, {
                    credentials: 'same-origin',
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                })
                    .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
                    .then(data => {
                        if (!data.hasNew) { return; }

                        // Страницу НЕ перезагружаем сами: человек может
                        // писать ответ, и обновление стёрло бы набранное.
                        // Предлагаем — решает он.
                        told = true;

                        toast('В переписке есть новые сообщения', 'info',
                            'Показать', () => window.location.reload());
                    })
                    .catch(() => { /* связь могла моргнуть — молчим */ });
            }, CHECK_MS);
        }
    }

    /** Сколько ждём ответа, прежде чем признать, что связи нет. */
    const WAIT_MS = 45000;

    function openPreview(item, node) {
        const source = '/Files?handler=Preview&fileId=' + item.file;

        // Значок берём прямо из плитки: он уже нарисован сервером,
        // и рисовать его второй раз здесь — лишнее удвоение кода,
        // которое неминуемо разъедется с серверным.
        const icon = node ? node.querySelector('.tile-icon svg, .ficon svg') : null;

        const m = openModal({
            wide: true,
            title: `<span class="pv-title">${icon ? icon.outerHTML : ''}<span>${esc(item.name)}</span></span>`,
            body: '<div class="pv-body">' +
                '<div class="pv-viewer" id="pvViewer">' +
                '<div class="pv-load"><div class="pv-spin"></div><p>Загружается…</p></div>' +
                '</div>' +
                '<div class="pv-aside">' +
                '<div class="pv-actions">' +
                `<button class="btn sm" type="button" id="pvFav">${ic('star', 14)}Избранное</button>` +
                `<button class="btn sm" type="button" id="pvShare">${ic('share', 14)}Ссылка</button>` +
                `<a class="btn sm" id="pvDl" href="${esc(item.href)}" download>${ic('download', 14)}</a>` +
                '</div>' +
                '<div class="pv-meta" id="pvFacts"><h4>Сведения</h4></div>' +
                '<div class="pv-meta" id="pvAccess"><h4>Доступ</h4></div>' +
                '</div></div>'
        });

        const viewer = $('#pvViewer', m);

        loadFacts(item, m);
        loadViewer(item, source, viewer);

        $('#pvFav', m).onclick = () => {
            post('/Files?handler=Favorite&fileId=' + item.file, {})
                .then(r => r.json())
                .then(d => toast(d.favorite ? 'Добавлено в избранное' : 'Убрано из избранного', 'info'))
                .catch(() => toast('Не удалось изменить избранное', 'danger'));
        };

        $('#pvShare', m).onclick = () => {
            // Ссылка именно НА ФАЙЛ, а не на страницу папки: по ней файл
            // сразу откроется или скачается. Ссылка обычная, не публичная —
            // анонимного доступа портал не даёт, и откроет её только тот,
            // у кого и так есть доступ к папке.
            copyText(location.origin + item.href, 'Ссылка на файл скопирована');
        };
    }

    /** Колонки «Сведения» и «Доступ» — те же данные, что и в окне свойств. */
    function loadFacts(item, m) {
        fetch('/Files?handler=Properties&fileId=' + item.file, {
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'XMLHttpRequest' }
        })
            .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
            .then(data => {
                $('#pvFacts', m).innerHTML = '<h4>Сведения</h4>' +
                    (data.rows || []).map(row =>
                        `<div class="row"><span>${esc(row.name)}</span><b>${esc(row.value)}</b></div>`).join('');

                const groups = data.access || [];

                $('#pvAccess', m).innerHTML = '<h4>Доступ</h4>' +
                    (groups.length
                        ? groups.map(g => `<div class="acc-row">${esc(g)}</div>`).join('')
                        : '<div class="acc-row">Действуют права родительской папки</div>');
            })
            .catch(() => {
                // Сведения — дополнение, а не главное. Не получилось —
                // окно всё равно показывает файл, ругаться незачем.
            });
    }

    function loadViewer(item, source, viewer) {
        const show = html => { viewer.innerHTML = html; };

        const failed = (text, detail) => {
            viewer.innerHTML = `<div class="empty">${ic('alert', 60, 1)}<b>${esc(text)}</b>` +
                `<p>${esc(detail || '')}</p>` +
                `<a class="btn" href="${esc(item.href)}" download>${ic('download', 14)}Скачать файл</a></div>`;
        };

        const html = (url, wrap) => {
            const controller = typeof AbortController === 'function' ? new AbortController() : null;
            let timedOut = false;

            const timer = setTimeout(() => {
                timedOut = true;
                if (controller) { controller.abort(); }
            }, WAIT_MS);

            const options = { credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } };

            if (controller) { options.signal = controller.signal; }

            fetch(url, options)
                .then(r => { clearTimeout(timer); return r.ok ? r.text() : Promise.reject(r.status); })
                .then(markup => show(wrap ? wrap(markup) : markup))
                .catch(reason => {
                    clearTimeout(timer);

                    failed('Не удалось получить содержимое',
                        timedOut ? 'ответ не пришёл за ' + Math.round(WAIT_MS / 1000) + ' с'
                            : reason === 401 ? 'вход в портал истёк — обновите страницу'
                                : 'сервер ответил кодом ' + reason);
                });
        };

        if (item.kind === 'image') {
            const image = new Image();

            image.onload = () => {
                show('<div class="pv-img"><img id="pvImg" alt=""></div>' +
                    '<div class="zoom-bar">' +
                    `<button class="icon-btn" type="button" data-z="-1">${ic('zoomout', 16)}</button>` +
                    '<button class="icon-btn zoom-val" type="button" data-z="0" id="zLbl">100%</button>' +
                    `<button class="icon-btn" type="button" data-z="1">${ic('zoomin', 16)}</button></div>`);

                $('#pvImg', viewer).src = source;

                let z = 1;

                $$('[data-z]', viewer).forEach(b => {
                    b.onclick = () => {
                        const d = +b.dataset.z;

                        z = d === 0 ? 1 : Math.min(3, Math.max(0.5, z + d * 0.25));

                        $('#pvImg', viewer).style.transform = `scale(${z})`;
                        $('#zLbl', viewer).textContent = Math.round(z * 100) + '%';
                    };
                });
            };

            image.onerror = () => failed('Не удалось показать изображение', 'файл не дошёл или повреждён');
            image.src = source;

            return;
        }

        if (item.kind === 'pdf') {
            // PDF показывает сам браузер. Это удобно, но и уязвимо:
            // встроенный просмотрщик можно отключить групповой политикой,
            // и тогда окно остаётся пустым БЕЗ ошибки. Поэтому ждём события
            // «загрузилось», а если его нет — предлагаем открыть отдельно.
            const frame = document.createElement('iframe');

            frame.title = item.name;
            frame.className = 'pv-frame';

            let shown = false;

            const giveUp = setTimeout(() => {
                if (!shown) {
                    failed('Не удалось показать PDF во встроенном окне',
                        'возможно, просмотр PDF отключён настройками браузера');
                }
            }, WAIT_MS);

            frame.addEventListener('load', () => { shown = true; clearTimeout(giveUp); });

            // Адрес задаём ДО вставки в страницу: пустой iframe, попав
            // в разметку, сразу выдаёт событие «загрузилось» про пустую
            // страницу, и мы бы приняли его за настоящее.
            frame.src = source;

            // Просмотрщику PDF нужна ВСЯ высота окна. Обычные отступы
            // и выравнивание по верху, годные для страницы документа,
            // превратили бы его в маленькое окошко внутри большого.
            viewer.classList.add('pv-viewer--frame');
            viewer.innerHTML = '';
            viewer.appendChild(frame);

            return;
        }

        if (item.kind === 'office') {
            html('/Files?handler=OfficePreview&fileId=' + item.file);
            return;
        }

        if (item.kind === 'archive') {
            html('/Files?handler=ArchivePreview&fileId=' + item.file);
            return;
        }

        if (item.kind === 'text') {
            html(source, text => '<div class="code-box">' +
                text.split('\n').map((line, i) =>
                    `<div><span class="ln">${String(i + 1).padStart(3, ' ')}</span>${esc(line)}</div>`).join('') +
                '</div>');

            return;
        }

        if (item.kind === 'video' || item.kind === 'audio') {
            // Проигрывает сам браузер. Файл идёт кусками (сервер отдаёт его
            // с поддержкой диапазонов), поэтому запись на сотни мегабайт
            // начинает играть сразу — важно на канале между офисами.
            const player = document.createElement(item.kind);

            player.controls = true;
            player.preload = 'metadata';
            player.className = 'player-media player-media--' + item.kind;
            player.src = source;

            player.addEventListener('error', () =>
                failed('Не удалось проиграть запись', 'браузер не понимает этот формат — скачайте файл'));

            if (item.kind === 'video') {
                viewer.classList.add('pv-viewer--frame');
            }

            viewer.innerHTML = '';
            viewer.appendChild(player);

            return;
        }

        failed('Такой файл браузер показать не умеет',
            'нажмите «Скачать» — файл откроется в своей программе');
    }
})();
