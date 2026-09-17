// Imports every backend module so that `ucode -c` compiles the whole library in one pass.
'use strict';

import * as util from 'zaprett.util';
import * as validate from 'zaprett.validate';
import * as config from 'zaprett.config';
import * as store from 'zaprett.store';
import * as strategy from 'zaprett.strategy';
import * as nft from 'zaprett.nft';
import * as offload from 'zaprett.offload';
import * as service from 'zaprett.service';
import * as job from 'zaprett.job';
import * as net from 'zaprett.net';
import * as repo from 'zaprett.repo';
import * as tester from 'zaprett.tester';
import * as commands from 'zaprett.commands';
import * as text from 'zaprett.text';
import * as sources from 'zaprett.sources';

print(sprintf('modules: %d\n', length([ util, validate, config, store, strategy, nft, offload, service, job, net,
	repo, tester, commands, text, sources ])));
