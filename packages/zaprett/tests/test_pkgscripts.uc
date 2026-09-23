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
	// сначала более длинный путь; системные файлы пользователей тоже подменяются — иначе тест правил бы /etc/passwd стенда
	let paths = [ UCODE, SHARE, '/var/lock/passwd', '/etc/passwd', '/etc/shadow', '/etc/group' ];
	let zroot = W + '/zaprett-tree';
	let tree = [ SHARE + '/bundle/files/lists/include', SHARE + '/bundle/files/ipset/exclude', SHARE + '/bundle/manifests/bin',
		SHARE + '/guard', SHARE + '/lua', UCODE, '/var/lock' ];

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
	system([ 'mkdir', '-p', zroot + '/var/lock' ]);
	T.eq(run_script(zscript, zroot, paths), 0, 'zaprett: отсутствующие каталоги — код возврата 0');

	// пользователь проверок автоподбора (контракт v1.6 §17): удаляются только его строки
	system([ 'mkdir', '-p', zroot + '/etc', zroot + '/var/lock' ]);
	let pw = 'root:x:0:0:root:/root:/bin/ash\nzaprett-testx:x:5:5::/:/bin/false\nzaprett-test:x:29411:29411:zaprett-test:/var:/bin/false\n';
	fs.writefile(zroot + '/etc/passwd', pw);
	fs.writefile(zroot + '/etc/shadow', 'root:x:0:0:99999:7:::\nzaprett-test:x:0:0:99999:7:::\n');
	fs.writefile(zroot + '/etc/group', 'root:x:0:\nzaprett-test:x:29411:\n');
	rc = run_script(zscript, zroot, paths);
	T.eq([ rc, fs.readfile(zroot + '/etc/passwd'), fs.readfile(zroot + '/etc/shadow'), fs.readfile(zroot + '/etc/group') ],
		[ 0, 'root:x:0:0:root:/root:/bin/ash\nzaprett-testx:x:5:5::/:/bin/false\n', 'root:x:0:0:99999:7:::\n', 'root:x:0:\n' ],
		'zaprett: строки zaprett-test удалены из passwd, shadow, group; похожее имя и root не тронуты');
	T.eq(run_script(zscript, zroot, paths), 0, 'zaprett: повторное удаление без пользователя — код возврата 0');
	T.ok(index(zscript, '/etc/passwd') >= 0 && index(zscript, "'/^zaprett-test:/d'") >= 0, 'postrm удаляет строки только по точному имени');
}

/* ---- uci-defaults: пользователь zaprett-test создаётся один раз, чужой uid не занимается ---- */
let ud = fs.readfile(T.ROOT + '/files/etc/uci-defaults/90-zaprett');
let ub = index(ud, 'zt_id=29411');
let ue = (ub >= 0) ? index(substr(ud, ub), '\nfi\n') : -1;
if (T.ok(ub >= 0 && ue > 0, 'uci-defaults: блок создания пользователя найден')) {
	let block = substr(ud, ub, ue + 4);
	let uroot = W + '/uci-defaults-user';
	let run_block = (passwd, group) => {
		system([ 'rm', '-rf', uroot ]);
		system([ 'mkdir', '-p', uroot + '/etc' ]);
		fs.writefile(uroot + '/etc/passwd', passwd);
		fs.writefile(uroot + '/etc/group', group);
		fs.writefile(uroot + '/etc/shadow', '');
		let sh = block;
		for (let p in [ '/etc/passwd', '/etc/group' ])
			sh = join(uroot + p, split(sh, p));
		fs.writefile(uroot + '/block.sh', sh);
		// user_add/group_add из /lib/functions.sh пишут в ${IPKG_INSTROOT}/etc/… — это песочница
		return system([ 'env', 'IPKG_INSTROOT=' + uroot, 'sh', uroot + '/block.sh' ]);
	};
	let base_pw = 'root:x:0:0:root:/root:/bin/ash\n', base_gr = 'root:x:0:\n';
	if (fs.stat('/lib/functions.sh')?.type == 'file') {
		run_block(base_pw, base_gr);
		T.eq([ fs.readfile(uroot + '/etc/passwd'), fs.readfile(uroot + '/etc/group'), fs.readfile(uroot + '/etc/shadow') ],
			[ base_pw + 'zaprett-test:x:29411:29411:zaprett-test:/var:/bin/false\n', base_gr + 'zaprett-test:x:29411:\n',
			  'zaprett-test:x:0:0:99999:7:::\n' ], 'uci-defaults: пользователь и группа 29411 созданы');
		let once = fs.readfile(uroot + '/etc/passwd');
		let sh2 = fs.readfile(uroot + '/block.sh');
		system([ 'env', 'IPKG_INSTROOT=' + uroot, 'sh', uroot + '/block.sh' ]);
		T.eq([ fs.readfile(uroot + '/etc/passwd'), length(split(fs.readfile(uroot + '/etc/group'), 'zaprett-test')) ], [ once, 2 ],
			'uci-defaults: повторный запуск ничего не добавляет' + (sh2 ? '' : ' (нет скрипта)'));
		run_block(base_pw + 'other:x:29411:100::/:/bin/false\n', base_gr);
		T.ok(index(fs.readfile(uroot + '/etc/passwd'), 'zaprett-test') < 0, 'отрицательный контроль: uid 29411 занят — пользователь не создаётся');
		run_block(base_pw, base_gr + 'other:x:29411:\n');
		T.ok(index(fs.readfile(uroot + '/etc/passwd'), 'zaprett-test') < 0 && index(fs.readfile(uroot + '/etc/group'), 'zaprett-test') < 0,
			'отрицательный контроль: gid 29411 занят чужой группой — ни группы, ни пользователя');
	}
	else
		print('SKIP [pkgscripts] нет /lib/functions.sh: создание пользователя не проверено\n');
}

exit(T.finish());
