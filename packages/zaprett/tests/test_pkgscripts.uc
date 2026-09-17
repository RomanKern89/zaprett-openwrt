'use strict';

// Скрипты пакетов движков: после удаления не должно оставаться пустых каталогов.
// Тело `define Package/<имя>/postrm` берётся из настоящего Makefile, абсолютные пути подменяются на
// песочницу, и скрипт запускается — проверяется именно тот текст, который попадёт в пакет.
import * as fs from 'fs';
import * as T from 'ztest';

T.begin('pkgscripts');
T.selfcheck();
let W = T.sandbox('pkgscripts');

const LIBEXEC = '/usr/libexec/zaprett';
const LUADIR = '/usr/share/zaprett/lua';

function makefile_of(pkg) {
	return T.ROOT + '/../' + pkg + '/Makefile';
}

// Pure: тело define-блока Makefile без подстановки переменных.
function script_of(path, pkg, section) {
	let text = fs.readfile(path, 262144);
	if (text == null)
		return null;
	let head = 'define Package/' + pkg + '/' + section + '\n';
	let start = index(text, head);
	if (start < 0)
		return null;
	let body = substr(text, start + length(head));
	let e = index(body, '\nendef');
	return (e < 0) ? null : substr(body, 0, e + 1);
}

// Текст из Makefile: $$VAR -> $VAR, абсолютные пути -> внутрь песочницы (сначала самые длинные).
// Только split/join: в replace() символ `$` в строке замены имеет особый смысл и `'$'` превращает `$$` в пустоту.
function run_script(text, root, paths) {
	let sh = join('$', split(text, '$$'));
	for (let p in paths ?? [ LUADIR, LIBEXEC ])
		sh = join(root + p, split(sh, p));
	let path = root + '/postrm.sh';
	fs.writefile(path, sh);
	return system([ 'sh', path ]);
}

function reset(root, files) {
	system([ 'rm', '-rf', root ]);
	system([ 'mkdir', '-p', root + LIBEXEC, root + LUADIR ]);
	for (let f in files)
		fs.writefile(root + f, 'x');
}

let missing = filter([ 'zaprett-nfqws', 'zaprett-nfqws2' ], (p) => fs.stat(makefile_of(p)) == null);
let engines = length(missing) ? [] : [ 'zaprett-nfqws', 'zaprett-nfqws2' ];
if (length(missing))
	print('SKIP [pkgscripts] нет Makefile пакетов движков рядом с корнем: ' + join(', ', missing) + '\n');

for (let pkg in engines) {
	let script = script_of(makefile_of(pkg), pkg, 'postrm');
	if (!T.ok(script != null, pkg + ': в Makefile есть define Package/' + pkg + '/postrm'))
		continue;
	T.ok(index(script, 'IPKG_INSTROOT') >= 0, pkg + ': postrm ничего не делает при сборке образа (IPKG_INSTROOT)');
	T.ok(index(script, 'rmdir') >= 0 && index(script, 'rmdir -p') < 0 && index(script, 'rm -r') < 0,
		pkg + ': каталог снимается только rmdir без -p, без rm -r');
	let root = W + '/' + pkg;

	// пустой каталог движков удаляется
	reset(root, []);
	let rc = run_script(script, root);
	T.eq([ rc, fs.stat(root + LIBEXEC) ], [ 0, null ], pkg + ': пустой ' + LIBEXEC + ' удалён');

	// отрицательный контроль: второй движок ещё установлен — каталог остаётся вместе с файлом
	reset(root, [ LIBEXEC + '/nfqws-other' ]);
	rc = run_script(script, root);
	T.eq([ rc, fs.stat(root + LIBEXEC)?.type, fs.stat(root + LIBEXEC + '/nfqws-other')?.type ],
		[ 0, 'directory', 'file' ], pkg + ': непустой ' + LIBEXEC + ' не удалён');

	// каталога уже нет — скрипт не ругается
	system([ 'rm', '-rf', root ]);
	system([ 'mkdir', '-p', root ]);
	T.eq(run_script(script, root), 0, pkg + ': отсутствующий каталог — код возврата 0');
}

// nfqws2 отвечает ещё и за свой каталог lua, но не за /usr/share/zaprett пакета zaprett
if (length(engines)) {
	let s2 = script_of(makefile_of('zaprett-nfqws2'), 'zaprett-nfqws2', 'postrm');
	let root2 = W + '/lua-case';
	reset(root2, []);
	run_script(s2, root2);
	T.eq([ fs.stat(root2 + LUADIR), fs.stat(root2 + '/usr/share/zaprett')?.type ], [ null, 'directory' ],
		'nfqws2: пустой каталог lua удалён, /usr/share/zaprett оставлен');
	reset(root2, [ LUADIR + '/zapret-lib.lua.gz' ]);
	run_script(s2, root2);
	T.eq(fs.stat(root2 + LUADIR)?.type, 'directory', 'отрицательный контроль: каталог lua с файлом не удалён');
	T.ok(index(script_of(makefile_of('zaprett-nfqws'), 'zaprett-nfqws', 'postrm'), LUADIR) < 0,
		'nfqws не трогает каталог lua чужого пакета');
}

/* ---- postrm самого пакета zaprett: своё дерево каталогов ---- */
const SHARE = '/usr/share/zaprett';
const UCODE = '/usr/share/ucode/zaprett';
let zscript = script_of(T.ROOT + '/Makefile', 'zaprett', 'postrm');
if (T.ok(zscript != null, 'zaprett: в Makefile есть define Package/zaprett/postrm')) {
	T.ok(index(zscript, 'IPKG_INSTROOT') >= 0, 'zaprett: postrm ничего не делает при сборке образа');
	T.ok(index(zscript, 'rmdir') >= 0 && index(zscript, 'rm -r') < 0 && index(zscript, 'rmdir -p') < 0,
		'zaprett: только rmdir без -p, без rm -r');
	T.ok(index(zscript, '/etc/zaprett') < 0, 'zaprett: postrm не трогает /etc/zaprett (его снимает install.sh --purge)');
	let paths = [ UCODE, SHARE ];	// сначала более длинный путь
	let zroot = W + '/zaprett-tree';
	let tree = [ SHARE + '/bundle/files/lists/include', SHARE + '/bundle/files/ipset/exclude', SHARE + '/bundle/manifests/bin',
		SHARE + '/guard', SHARE + '/lua', UCODE ];

	// каталог lua принадлежит zaprett-nfqws2: он и его родители остаются
	system([ 'rm', '-rf', zroot ]);
	for (let d in tree)
		system([ 'mkdir', '-p', zroot + d ]);
	fs.writefile(zroot + SHARE + '/lua/zapret-lib.lua.gz', 'x');
	let rc = run_script(zscript, zroot, paths);
	T.eq([ rc, fs.stat(zroot + SHARE + '/bundle/files/lists/include'), fs.stat(zroot + SHARE + '/bundle'),
		fs.stat(zroot + UCODE) ], [ 0, null, null, null ], 'zaprett: пустые каталоги своего дерева удалены');
	T.eq([ fs.stat(zroot + SHARE + '/lua')?.type, fs.stat(zroot + SHARE + '/lua/zapret-lib.lua.gz')?.type,
		fs.stat(zroot + SHARE)?.type ], [ 'directory', 'file', 'directory' ],
		'отрицательный контроль: каталог с файлом другого пакета и его родитель сохранены вместе с файлом');

	// без чужих файлов снимается всё дерево, включая корень
	system([ 'rm', '-rf', zroot ]);
	for (let d in tree)
		system([ 'mkdir', '-p', zroot + d ]);
	rc = run_script(zscript, zroot, paths);
	T.eq([ rc, fs.stat(zroot + SHARE), fs.stat(zroot + UCODE), fs.stat(zroot + '/usr/share')?.type ],
		[ 0, null, null, 'directory' ], 'zaprett: пустое дерево снято целиком, /usr/share не тронут');

	// каталогов уже нет — код возврата 0
	system([ 'rm', '-rf', zroot ]);
	system([ 'mkdir', '-p', zroot ]);
	T.eq(run_script(zscript, zroot, paths), 0, 'zaprett: отсутствующие каталоги — код возврата 0');
}

exit(T.finish());
