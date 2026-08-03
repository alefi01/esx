/*
 * Код страниц портала.
 *
 * Написан на обычном JavaScript, без единой библиотеки. Причина та же,
 * что и у стилей: в вашей сети нет интернета, а значит нет ни npm,
 * ни возможности подтянуть что-то с CDN. Зато этот файл можно прочитать
 * целиком за один присест и поправить, не изучая чужой фреймворк.
 *
 * Файл подключается один раз в общей разметке (_Layout.cshtml) и сам
 * определяет, что на текущей странице есть, а чего нет.
 *
 * Содержание:
 *   1. Мелкие помощники
 *   2. Полоса загрузки страницы
 *   3. Всплывающие сообщения
 *   4. Модальные окна
 *   5. Контекстное меню
 *   6. Файловый менеджер: выделение, буфер обмена, перетаскивание, загрузка
 */

(function () {
    'use strict';

    // ======================================================================
    // 1. Мелкие помощники
    // ======================================================================

    const $ = (selector, root) => (root || document).querySelector(selector);
    const $$ = (selector, root) => Array.from((root || document).querySelectorAll(selector));

    /** Человекочитаемый размер: 12,3 МБ вместо 12897485. */
    function formatSize(bytes) {
        const units = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
        let value = bytes;
        let unit = 0;

        while (value >= 1024 && unit < units.length - 1) {
            value /= 1024;
            unit++;
        }

        return unit === 0
            ? bytes + ' ' + units[unit]
            : value.toFixed(1).replace('.', ',').replace(',0', '') + ' ' + units[unit];
    }

    /**
     * Токен защиты форм от подделки. Берётся из любой формы на странице:
     * ASP.NET Core кладёт его в каждую скрытым полем, а без него сервер
     * отклонит наш запрос — и правильно сделает.
     */
    function antiforgeryToken() {
        const field = $('input[name="__RequestVerificationToken"]');
        return field ? field.value : '';
    }

    /** Отправляет скрытую форму, добавив в неё нужные поля. */
    function submitForm(form, fields) {
        if (!form) {
            return;
        }

        // Убираем поля, добавленные при прошлой отправке.
        $$('[data-temp]', form).forEach(node => node.remove());

        Object.keys(fields).forEach(name => {
            const values = Array.isArray(fields[name]) ? fields[name] : [fields[name]];

            values.forEach(value => {
                const input = document.createElement('input');
                input.type = 'hidden';
                input.name = name;
                input.value = value;
                input.dataset.temp = '1';
                form.appendChild(input);
            });
        });

        progress.start();
        form.submit();
    }

    // ======================================================================
    // 2. Полоса загрузки страницы
    //
    // Тонкая полоска сверху, пока страница переходит по ссылке или
    // отправляет форму. Нужна не для красоты: на канале между офисами
    // ответ может идти секунду-две, и без отклика человек жмёт кнопку
    // второй раз.
    // ======================================================================

    const progress = (function () {
        let bar = null;
        let timer = null;

        function ensure() {
            if (!bar) {
                bar = document.createElement('div');
                bar.className = 'pageprogress';
                document.body.appendChild(bar);
            }
            return bar;
        }

        return {
            start() {
                const element = ensure();
                element.classList.remove('is-done');
                element.classList.add('is-active');

                clearTimeout(timer);
                // Если ответ так и не пришёл, полоска не должна висеть вечно.
                timer = setTimeout(() => this.done(), 30000);
            },
            done() {
                if (!bar) {
                    return;
                }
                clearTimeout(timer);
                bar.classList.remove('is-active');
                bar.classList.add('is-done');
            }
        };
    })();

    // Показываем полоску при обычных переходах и отправке форм.
    document.addEventListener('click', event => {
        const link = event.target.closest('a[href]');

        if (!link || link.target === '_blank' || event.ctrlKey || event.metaKey || event.button !== 0) {
            return;
        }

        // Скачивание файла страницу не меняет — полоска только запутает.
        if (link.href.indexOf('handler=Download') !== -1) {
            return;
        }

        if (link.origin === location.origin) {
            progress.start();
        }
    });

    document.addEventListener('submit', event => {
        progress.start();

        // Кнопка отправки переходит в состояние «занято»: и видно, что
        // нажатие принято, и второй раз нажать уже нельзя.
        const button = event.target.querySelector('button[type="submit"]');

        if (button && event.target.hasAttribute('data-busy-form')) {
            button.classList.add('is-busy');
            button.disabled = true;
        }
    });

    // Возврат по кнопке «назад» отдаёт страницу из кэша — полоску надо убрать.
    window.addEventListener('pageshow', () => progress.done());

    // ======================================================================
    // 3. Всплывающие сообщения
    // ======================================================================

    const toasts = (function () {
        let host = null;

        function ensure() {
            if (!host) {
                host = document.createElement('div');
                host.className = 'toasts';
                document.body.appendChild(host);
            }
            return host;
        }

        return {
            show(text, kind) {
                const toast = document.createElement('div');
                toast.className = 'toast toast--' + (kind || 'info');
                toast.textContent = text;
                ensure().appendChild(toast);

                // Даём браузеру отрисовать элемент, и только потом включаем
                // появление — иначе перехода не будет, элемент сразу окажется на месте.
                requestAnimationFrame(() => toast.classList.add('is-visible'));

                setTimeout(() => {
                    toast.classList.remove('is-visible');
                    setTimeout(() => toast.remove(), 300);
                }, kind === 'error' ? 8000 : 4000);
            }
        };
    })();

    // ======================================================================
    // 4. Модальные окна
    // ======================================================================

    function openModal(name) {
        const modal = $('[data-modal="' + name + '"]');

        if (!modal) {
            return;
        }

        modal.hidden = false;
        requestAnimationFrame(() => modal.classList.add('is-open'));

        const focusTarget = $('[data-autofocus]', modal);

        if (focusTarget) {
            focusTarget.focus();
            focusTarget.select();
        }
    }

    function closeModal(modal) {
        modal.classList.remove('is-open');
        setTimeout(() => { modal.hidden = true; }, 200);
    }

    document.addEventListener('click', event => {
        if (event.target.closest('[data-modal-close]')) {
            const modal = event.target.closest('[data-modal]');
            if (modal) {
                closeModal(modal);
            }
        }
    });

    /**
     * Окно подтверждения вместо системного confirm().
     * Системное окно выглядит по-разному в каждом браузере и выбивается
     * из оформления; здесь же и вид единый, и текст можно написать нормальный.
     * Возвращает обещание: true — подтвердили, false — отказались.
     */
    function confirmDialog(text, okLabel) {
        return new Promise(resolve => {
            const modal = $('[data-modal="confirm"]');

            if (!modal) {
                // Если окна на странице нет, лучше спросить хоть как-то,
                // чем молча выполнить необратимое действие.
                resolve(window.confirm(text));
                return;
            }

            $('[data-confirm-text]', modal).textContent = text;

            const okButton = $('[data-confirm-ok]', modal);
            okButton.textContent = okLabel || 'Удалить';

            let settled = false;

            function finish(result) {
                if (settled) {
                    return;
                }
                settled = true;

                okButton.removeEventListener('click', onOk);
                modal.removeEventListener('click', onCancel);
                closeModal(modal);
                resolve(result);
            }

            function onOk() { finish(true); }

            function onCancel(event) {
                if (event.target.closest('[data-modal-close]')) {
                    finish(false);
                }
            }

            okButton.addEventListener('click', onOk);
            modal.addEventListener('click', onCancel);

            openModal('confirm');
            okButton.focus();
        });
    }

    document.addEventListener('keydown', event => {
        if (event.key === 'Escape') {
            const open = $('[data-modal].is-open');
            if (open) {
                closeModal(open);
            }
            hideMenu();
        }
    });

    // ======================================================================
    // 5. Контекстное меню
    // ======================================================================

    const menu = $('[data-context-menu]');

    function hideMenu() {
        if (menu) {
            menu.hidden = true;
            menu.classList.remove('is-open');
        }
    }

    /**
     * Показывает меню в точке нажатия.
     * items: [{ label, icon, danger, onClick }] либо { separator: true }
     */
    function showMenu(x, y, items) {
        if (!menu || items.length === 0) {
            return;
        }

        menu.innerHTML = '';

        items.forEach(item => {
            if (item.separator) {
                const separator = document.createElement('div');
                separator.className = 'menu__sep';
                menu.appendChild(separator);
                return;
            }

            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'menu__item' + (item.danger ? ' menu__item--danger' : '');
            button.setAttribute('role', 'menuitem');

            if (item.icon) {
                const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                svg.setAttribute('class', 'icon');
                const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
                use.setAttribute('href', '#' + item.icon);
                svg.appendChild(use);
                button.appendChild(svg);
            }

            button.appendChild(document.createTextNode(item.label));

            button.addEventListener('click', () => {
                hideMenu();
                item.onClick();
            });

            menu.appendChild(button);
        });

        menu.hidden = false;

        // Сначала показываем, потом измеряем: у скрытого элемента размеров нет.
        const rect = menu.getBoundingClientRect();
        const left = Math.min(x, window.innerWidth - rect.width - 8);
        const top = Math.min(y, window.innerHeight - rect.height - 8);

        menu.style.left = Math.max(8, left) + 'px';
        menu.style.top = Math.max(8, top) + 'px';

        requestAnimationFrame(() => menu.classList.add('is-open'));
    }

    document.addEventListener('click', hideMenu);
    window.addEventListener('resize', hideMenu);
    window.addEventListener('scroll', hideMenu, true);

    // ======================================================================
    // 6. Файловый менеджер
    // ======================================================================

    const explorer = $('[data-explorer]');

    if (explorer) {
        initExplorer(explorer);
    }

    function initExplorer(root) {
        const folderId = root.dataset.folderId ? parseInt(root.dataset.folderId, 10) : null;
        const canWrite = root.dataset.canWrite === 'true';
        const canManage = root.dataset.canManage === 'true';
        const canCreateFolder = root.dataset.canCreateFolder === 'true';
        const maxSize = parseInt(root.dataset.maxSize, 10) || 0;

        const pane = $('[data-drop-zone]', root);
        const veil = $('[data-drop-veil]', root);
        const fileInput = $('#file-input');

        // Ширину полосы квоты задаём здесь, а не в разметке: встроенные
        // стили в HTML запрещены политикой безопасности страницы.
        $$('.quota__fill').forEach(fill => {
            fill.style.width = (fill.dataset.percent || 0) + '%';
        });

        // ---------- Выделение ----------

        let selection = new Set();
        let lastClicked = null;

        function tiles() {
            return $$('.tile', pane);
        }

        function fileTiles() {
            return $$('.tile--file', pane);
        }

        function selectedFileIds() {
            return Array.from(selection);
        }

        function repaintSelection() {
            fileTiles().forEach(tile => {
                tile.classList.toggle('is-selected', selection.has(parseInt(tile.dataset.file, 10)));
            });
        }

        function clearSelection() {
            selection.clear();
            repaintSelection();
        }

        function selectRange(fromTile, toTile) {
            const all = fileTiles();
            const from = all.indexOf(fromTile);
            const to = all.indexOf(toTile);

            if (from === -1 || to === -1) {
                return;
            }

            const [start, end] = from < to ? [from, to] : [to, from];

            for (let i = start; i <= end; i++) {
                selection.add(parseInt(all[i].dataset.file, 10));
            }
        }

        // Одиночное нажатие выделяет файл, а не открывает его: так же,
        // как в проводнике Windows. Открывается файл двойным нажатием.
        pane.addEventListener('click', event => {
            const tile = event.target.closest('.tile--file');

            if (!tile) {
                if (!event.target.closest('.tile')) {
                    clearSelection();
                }
                return;
            }

            event.preventDefault();

            const id = parseInt(tile.dataset.file, 10);

            if (event.shiftKey && lastClicked) {
                selectRange(lastClicked, tile);
            } else if (event.ctrlKey || event.metaKey) {
                if (selection.has(id)) {
                    selection.delete(id);
                } else {
                    selection.add(id);
                }
                lastClicked = tile;
            } else {
                selection = new Set([id]);
                lastClicked = tile;
            }

            repaintSelection();
        });

        // Двойное нажатие — открыть: скачать файл или зайти в папку.
        pane.addEventListener('dblclick', event => {
            const tile = event.target.closest('.tile');

            if (tile) {
                window.location.href = tile.href;
            }
        });

        // ---------- Контекстное меню ----------

        pane.addEventListener('contextmenu', event => {
            const fileTile = event.target.closest('.tile--file');
            const folderTile = event.target.closest('.tile--folder');

            event.preventDefault();

            if (fileTile) {
                const id = parseInt(fileTile.dataset.file, 10);

                // Нажатие правой кнопкой по невыделенному файлу сначала выделяет его.
                if (!selection.has(id)) {
                    selection = new Set([id]);
                    lastClicked = fileTile;
                    repaintSelection();
                }

                showMenu(event.clientX, event.clientY, fileMenuItems(fileTile));
                return;
            }

            if (folderTile) {
                showMenu(event.clientX, event.clientY, folderMenuItems(folderTile));
                return;
            }

            clearSelection();
            showMenu(event.clientX, event.clientY, emptySpaceMenuItems());
        });

        function fileMenuItems(tile) {
            const ids = selectedFileIds();
            const canDelete = tile.dataset.canDelete === 'true';
            const items = [];

            if (ids.length === 1) {
                items.push({
                    label: 'Скачать', icon: 'i-download',
                    onClick: () => { window.location.href = tile.href; }
                });
            }

            items.push({
                label: 'Копировать' + countSuffix(ids.length), icon: 'i-copy',
                onClick: () => putToClipboard('copy', ids)
            });

            if (canDelete) {
                items.push({
                    label: 'Вырезать' + countSuffix(ids.length), icon: 'i-cut',
                    onClick: () => putToClipboard('cut', ids)
                });

                items.push({ separator: true });

                items.push({
                    label: 'Удалить' + countSuffix(ids.length), icon: 'i-trash', danger: true,
                    onClick: () => deleteSelected()
                });
            }

            return items;
        }

        function folderMenuItems(tile) {
            const id = parseInt(tile.dataset.folder, 10);
            const items = [
                {
                    label: 'Открыть', icon: 'i-open',
                    onClick: () => { window.location.href = tile.href; }
                }
            ];

            if (canManage) {
                items.push({
                    label: 'Управление папкой', icon: 'i-settings',
                    onClick: () => { window.location.href = '/Files/Settings/' + id; }
                });

                items.push({ separator: true });

                items.push({
                    label: 'Удалить папку', icon: 'i-trash', danger: true,
                    onClick: () => {
                        confirmDialog(
                            'Удалить папку «' + tile.dataset.name + '»? ' +
                            'Удалить можно только пустую папку — без подпапок и файлов, включая корзину.',
                            'Удалить папку'
                        ).then(ok => {
                            if (ok) {
                                submitForm($('[data-form="delete-folder"]'), { folderId: id });
                            }
                        });
                    }
                });
            }

            return items;
        }

        function emptySpaceMenuItems() {
            const items = [];

            if (canWrite) {
                items.push({
                    label: 'Загрузить файлы', icon: 'i-upload',
                    onClick: () => fileInput && fileInput.click()
                });
            }

            if (canCreateFolder) {
                items.push({
                    label: 'Создать папку', icon: 'i-new-folder',
                    onClick: () => openModal('new-folder')
                });
            }

            const clipboard = readClipboard();

            if (canWrite && clipboard && clipboard.fileIds.length > 0 && clipboard.folderId !== folderId) {
                items.push({ separator: true });
                items.push({
                    label: (clipboard.op === 'cut' ? 'Переместить сюда' : 'Вставить') +
                        countSuffix(clipboard.fileIds.length),
                    icon: 'i-paste',
                    onClick: () => pasteFromClipboard()
                });
            }

            if (canManage) {
                items.push({ separator: true });
                items.push({
                    label: 'Управление папкой', icon: 'i-settings',
                    onClick: () => { window.location.href = '/Files/Settings/' + folderId; }
                });
            }

            return items;
        }

        function countSuffix(count) {
            return count > 1 ? ' (' + count + ')' : '';
        }

        // ---------- Буфер обмена внутри портала ----------
        //
        // Хранится в sessionStorage: переживает переход в другую папку,
        // но не переживает закрытие вкладки — ровно как буфер обмена
        // в проводнике не переживает перезагрузку.

        function putToClipboard(op, fileIds) {
            sessionStorage.setItem('portal.clipboard', JSON.stringify({
                op: op,
                fileIds: fileIds,
                folderId: folderId
            }));

            toasts.show(
                (op === 'cut' ? 'Вырезано файлов: ' : 'Скопировано файлов: ') + fileIds.length +
                '. Откройте нужную папку и нажмите Ctrl+V.',
                'info');
        }

        function readClipboard() {
            try {
                const raw = sessionStorage.getItem('portal.clipboard');
                return raw ? JSON.parse(raw) : null;
            } catch (error) {
                return null;
            }
        }

        function pasteFromClipboard() {
            const clipboard = readClipboard();

            if (!clipboard || clipboard.fileIds.length === 0 || folderId === null) {
                return;
            }

            if (clipboard.folderId === folderId) {
                toasts.show('Это та же папка, откуда файлы были взяты.', 'info');
                return;
            }

            const form = $('[data-form="' + (clipboard.op === 'cut' ? 'move' : 'copy') + '"]');

            // После перемещения буфер очищаем: повторная вставка вырезанного
            // ничего бы не нашла и выдала бы непонятную ошибку.
            if (clipboard.op === 'cut') {
                sessionStorage.removeItem('portal.clipboard');
            }

            submitForm(form, { targetFolderId: folderId, fileIds: clipboard.fileIds });
        }

        function deleteSelected() {
            const ids = selectedFileIds();

            if (ids.length === 0) {
                return;
            }

            const question = ids.length === 1
                ? 'Переместить файл в корзину? Восстановить его можно будет из раздела «Корзина».'
                : 'Переместить в корзину файлов: ' + ids.length +
                  '? Восстановить их можно будет из раздела «Корзина».';

            confirmDialog(question, 'В корзину').then(ok => {
                if (ok) {
                    submitForm($('[data-form="delete-files"]'), { fileIds: ids });
                }
            });
        }

        // ---------- Клавиатура ----------

        document.addEventListener('keydown', event => {
            // Не мешаем печатать в полях ввода.
            const tag = (event.target.tagName || '').toLowerCase();

            if (tag === 'input' || tag === 'textarea' || tag === 'select') {
                return;
            }

            if ($('[data-modal].is-open')) {
                return;
            }

            const ctrl = event.ctrlKey || event.metaKey;

            if (ctrl && event.key.toLowerCase() === 'a') {
                event.preventDefault();
                selection = new Set(fileTiles().map(tile => parseInt(tile.dataset.file, 10)));
                repaintSelection();
                return;
            }

            if (ctrl && event.key.toLowerCase() === 'c' && selection.size > 0) {
                event.preventDefault();
                putToClipboard('copy', selectedFileIds());
                return;
            }

            if (ctrl && event.key.toLowerCase() === 'x' && selection.size > 0) {
                event.preventDefault();
                putToClipboard('cut', selectedFileIds());
                return;
            }

            if (ctrl && event.key.toLowerCase() === 'v') {
                // Вставку файлов из проводника Windows обрабатывает событие paste
                // ниже; здесь — вставка того, что скопировали внутри портала.
                const clipboard = readClipboard();

                if (canWrite && clipboard && clipboard.fileIds.length > 0) {
                    event.preventDefault();
                    pasteFromClipboard();
                }
                return;
            }

            if (event.key === 'Delete' && selection.size > 0) {
                event.preventDefault();
                deleteSelected();
            }
        });

        // ---------- Вставка файлов из проводника (Ctrl+V) ----------

        document.addEventListener('paste', event => {
            if (!canWrite || !event.clipboardData) {
                return;
            }

            const files = Array.from(event.clipboardData.files || []);

            if (files.length > 0) {
                event.preventDefault();
                upload(files);
            }
        });

        // ---------- Перетаскивание ----------

        if (canWrite) {
            let dragDepth = 0;

            // dragenter/dragleave срабатывают и на вложенных элементах,
            // поэтому считаем «глубину»: подсказку убираем, только когда
            // указатель действительно покинул область.
            pane.addEventListener('dragenter', event => {
                if (!hasFiles(event)) {
                    return;
                }
                event.preventDefault();
                dragDepth++;
                veil.hidden = false;
                requestAnimationFrame(() => veil.classList.add('is-visible'));
            });

            pane.addEventListener('dragover', event => {
                if (hasFiles(event)) {
                    event.preventDefault();
                    event.dataTransfer.dropEffect = 'copy';
                }
            });

            pane.addEventListener('dragleave', () => {
                dragDepth = Math.max(0, dragDepth - 1);
                if (dragDepth === 0) {
                    hideVeil();
                }
            });

            pane.addEventListener('drop', event => {
                if (!hasFiles(event)) {
                    return;
                }

                event.preventDefault();
                dragDepth = 0;
                hideVeil();

                upload(Array.from(event.dataTransfer.files || []));
            });

            // Браузер по умолчанию открывает файл, брошенный мимо области.
            // Это выкидывает человека со страницы — перехватываем.
            ['dragover', 'drop'].forEach(name => {
                document.addEventListener(name, event => {
                    if (!event.target.closest('[data-drop-zone]') && hasFiles(event)) {
                        event.preventDefault();
                    }
                });
            });
        }

        function hasFiles(event) {
            return event.dataTransfer && Array.from(event.dataTransfer.types || []).indexOf('Files') !== -1;
        }

        function hideVeil() {
            veil.classList.remove('is-visible');
            setTimeout(() => { veil.hidden = true; }, 200);
        }

        // ---------- Перетаскивание файлов на папку = перемещение ----------

        if (canWrite) {
            fileTiles().forEach(tile => {
                tile.draggable = true;

                tile.addEventListener('dragstart', event => {
                    const id = parseInt(tile.dataset.file, 10);

                    if (!selection.has(id)) {
                        selection = new Set([id]);
                        repaintSelection();
                    }

                    event.dataTransfer.setData('application/x-portal-files', JSON.stringify(selectedFileIds()));
                    event.dataTransfer.effectAllowed = 'move';
                    tile.classList.add('is-dragging');
                });

                tile.addEventListener('dragend', () => tile.classList.remove('is-dragging'));
            });

            $$('[data-drop-target]', pane).forEach(target => {
                target.addEventListener('dragover', event => {
                    if (Array.from(event.dataTransfer.types).indexOf('application/x-portal-files') !== -1) {
                        event.preventDefault();
                        event.dataTransfer.dropEffect = 'move';
                        target.classList.add('is-drop-target');
                    }
                });

                target.addEventListener('dragleave', () => target.classList.remove('is-drop-target'));

                target.addEventListener('drop', event => {
                    const raw = event.dataTransfer.getData('application/x-portal-files');

                    if (!raw) {
                        return;
                    }

                    event.preventDefault();
                    event.stopPropagation();
                    target.classList.remove('is-drop-target');

                    submitForm($('[data-form="move"]'), {
                        targetFolderId: parseInt(target.dataset.dropTarget, 10),
                        fileIds: JSON.parse(raw)
                    });
                });
            });
        }

        // ---------- Загрузка с показом хода выполнения ----------

        if (fileInput) {
            $$('[data-action="pick-files"]').forEach(button => {
                button.addEventListener('click', () => fileInput.click());
            });

            fileInput.addEventListener('change', () => {
                const files = Array.from(fileInput.files || []);

                if (files.length > 0) {
                    upload(files);
                }

                // Сбрасываем, иначе повторный выбор того же файла
                // не вызовет события change.
                fileInput.value = '';
            });
        }

        $$('[data-action="new-folder"]').forEach(button => {
            button.addEventListener('click', () => openModal('new-folder'));
        });

        function upload(files) {
            if (folderId === null) {
                toasts.show('Файлы можно загружать только внутрь папки.', 'error');
                return;
            }

            // Отсекаем заведомо слишком большое ещё до отправки: незачем
            // гонять сотню мегабайт по сети, чтобы получить отказ.
            const tooBig = files.filter(file => maxSize > 0 && file.size > maxSize);

            if (tooBig.length > 0) {
                toasts.show(
                    'Слишком большие файлы (предел ' + formatSize(maxSize) + '): ' +
                    tooBig.map(file => file.name).join(', '),
                    'error');
            }

            const allowed = files.filter(file => !(maxSize > 0 && file.size > maxSize));

            if (allowed.length === 0) {
                return;
            }

            const form = new FormData();
            form.append('__RequestVerificationToken', antiforgeryToken());
            allowed.forEach(file => form.append('uploads', file, file.name));

            const panel = showUploadPanel(allowed);
            const request = new XMLHttpRequest();

            request.open('POST', '/Files?handler=Upload&folderId=' + folderId);
            request.setRequestHeader('X-Requested-With', 'XMLHttpRequest');

            request.upload.addEventListener('progress', event => {
                if (event.lengthComputable) {
                    panel.setProgress(event.loaded / event.total);
                }
            });

            request.addEventListener('load', () => {
                panel.done();

                if (request.status !== 200) {
                    toasts.show('Загрузка не удалась (код ' + request.status + ').', 'error');
                    return;
                }

                let result = null;

                try {
                    result = JSON.parse(request.responseText);
                } catch (error) {
                    // Сервер ответил не тем, чего мы ждали, — просто обновим страницу.
                }

                if (result && result.rejected && result.rejected.length > 0) {
                    toasts.show(
                        'Не загружены: ' +
                        result.rejected.map(r => '«' + r.file + '» — ' + r.reason).join('; '),
                        'error');

                    // Даём прочитать сообщение, прежде чем страница обновится.
                    setTimeout(() => location.reload(), 6000);
                    return;
                }

                location.reload();
            });

            request.addEventListener('error', () => {
                panel.done();
                toasts.show('Загрузка прервана: нет связи с сервером.', 'error');
            });

            request.send(form);
        }

        function showUploadPanel(files) {
            const panel = document.createElement('div');
            panel.className = 'uploader';

            const total = files.reduce((sum, file) => sum + file.size, 0);

            panel.innerHTML =
                '<div class="uploader__title">Загрузка ' + files.length + ' файл(ов) · ' +
                formatSize(total) + '</div>' +
                '<div class="uploader__bar"><span class="uploader__fill"></span></div>' +
                '<div class="uploader__percent">0%</div>';

            document.body.appendChild(panel);
            requestAnimationFrame(() => panel.classList.add('is-visible'));

            const fill = $('.uploader__fill', panel);
            const percent = $('.uploader__percent', panel);

            return {
                setProgress(ratio) {
                    const value = Math.round(ratio * 100);
                    fill.style.width = value + '%';
                    percent.textContent = value + '%';
                },
                done() {
                    fill.style.width = '100%';
                    percent.textContent = 'готово';
                    setTimeout(() => {
                        panel.classList.remove('is-visible');
                        setTimeout(() => panel.remove(), 300);
                    }, 400);
                }
            };
        }
    }
})();
