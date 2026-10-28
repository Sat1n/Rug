function on_init() rug.log('[update-probe] INIT') end
function on_tick()
  rug.sleep(1000)
  local cycle = (rug.get_state('cycle') or 0) + 1
  rug.set_state('cycle', cycle)
  rug.set_state('before_count', cycle)
  rug.log('[update-probe] BEFORE '..cycle)
  local saved = rug.checkpoint.commit('panel-ready', '{"position":"after-panel-ready","ready":true}', '["panel-ready"]')
  assert(saved.status == 'committed', saved.error)
  rug.set_state('after_count', cycle)
  rug.log('[update-probe] AFTER '..cycle)
end
function on_stop() rug.log('[update-probe] STOP') end
