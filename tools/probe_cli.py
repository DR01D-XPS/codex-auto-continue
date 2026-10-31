"""Read-only integration probe. Never prints credentials or conversation contents."""
import json, subprocess, shutil, sys, threading, queue, tempfile, os, time

isolated = '--isolated-resume' in sys.argv
cli_args = [a for a in sys.argv[1:] if not a.startswith('--')]
cli = cli_args[0] if cli_args else shutil.which('codex')
if not cli:
    raise SystemExit('Codex CLI not found')
temp = tempfile.TemporaryDirectory(prefix='CodexAutoContinue-native-resume-') if isolated else None
env = os.environ.copy()
if temp:
    env['CODEX_HOME'] = temp.name
p = subprocess.Popen([cli, 'app-server', '--listen', 'stdio://'], stdin=subprocess.PIPE,
                     stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8',env=env)
q = queue.Queue()
threading.Thread(target=lambda: [q.put(line) for line in p.stdout], daemon=True).start()
threading.Thread(target=lambda: [None for line in p.stderr], daemon=True).start()
counter = 0
def call(method, params):
    global counter
    counter += 1
    p.stdin.write(json.dumps({'id': counter, 'method': method, 'params': params})+'\n')
    p.stdin.flush()
    while True:
        obj = json.loads(q.get(timeout=40))
        if obj.get('id') == counter:
            if 'error' in obj:
                raise RuntimeError(method+': '+str(obj['error'].get('code'))+' '+obj['error'].get('message',''))
            return obj['result']
try:
    init = call('initialize', {'clientInfo': {'name':'codex_autocontinue_probe','title':'Codex AutoContinue Probe','version':'1.0.0'}})
    p.stdin.write('{"method":"initialized","params":{}}\n'); p.stdin.flush()
    print('initialize: OK')
    if isolated:
        thread = call('thread/start', {'cwd':temp.name})['thread']
        try:
            resumed = call('thread/resume', {'threadId':thread['id']})['thread']
            assert resumed['id'] == thread['id'], 'thread id changed'
            assert len(resumed.get('turns',[])) == 0, 'unexpected model turn'
            print('ISOLATED EMPTY THREAD CHECK PASS: empty session resume supported; no model turns')
        except RuntimeError as error:
            if 'no rollout found' not in str(error):
                raise
            print('ISOLATED EMPTY THREAD CHECK PASS: CLI does not persist a thread before its first turn; resume returns no rollout found. Application handles waiting before first turn separately.')
        raise SystemExit(0)
    a = call('account/read', {'refreshToken':False})
    print('account.type:', (a.get('account') or {}).get('type'))
    print('account.plan:', (a.get('account') or {}).get('planType'))
    cfg = call('config/read', {'includeLayers':False})['config']
    print('config:', json.dumps({k: cfg.get(k) for k in ['model','model_provider','approval_policy','sandbox_mode','approvals_reviewer']},ensure_ascii=False))
    rates = call('account/rateLimits/read', {})
    print('rate response fields:', ', '.join(rates.keys()))
    print('ordinaryUsageAllowed:', rates.get('ordinaryUsageAllowed'))
    for name, bucket in (rates.get('rateLimitsByLimitId') or {'codex':rates.get('rateLimits',{})}).items():
        print('bucket:', name, json.dumps({k:bucket.get(k) for k in ['primary','secondary','rateLimitReachedType','spendControlReached']},ensure_ascii=False))
    sessions = call('thread/list', {'limit':50, 'sourceKinds':['cli','vscode','exec','appServer'], 'modelProviders':[]})
    counts = {}
    for t in sessions['data']:
        src = str(t.get('source')); counts[src] = counts.get(src,0)+1
    print('session source counts:', json.dumps(counts))
    print('pagination:', bool(sessions.get('nextCursor')))
    print('probe: PASS (no model turns)')
finally:
    p.stdin.close()
    try: p.wait(timeout=5)
    except subprocess.TimeoutExpired: p.kill(); p.wait()
    if temp:
        target, base = os.path.realpath(temp.name), os.path.realpath(tempfile.gettempdir())
        assert os.path.commonpath([target,base]) == base and os.path.basename(target).startswith('CodexAutoContinue-native-resume-')
        for attempt in range(10):
            try:
                temp.cleanup()
                break
            except PermissionError:
                if attempt == 9:
                    raise
                time.sleep(0.2)
