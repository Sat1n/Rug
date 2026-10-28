function on_init() error('update target must use on_restore, never on_init') end
function on_restore(checkpointJson)
  assert(string.find(checkpointJson, 'after-panel-ready', 1, true))
  assert(rug.get_state('before_count') == nil)
  rug.set_state('after_count', 1)
  rug.log('[update-v2] AFTER only; new on_restore continued verified node')
end
function on_tick() rug.sleep(60000) end
function on_stop() rug.log('[update-v2] STOP') end
