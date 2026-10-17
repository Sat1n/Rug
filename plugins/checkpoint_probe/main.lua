function on_init()
  rug.log('[checkpoint-probe] normal start; waiting 5s before BEFORE B; detector remains active')
  rug.sleep(5000)
  rug.press_key(66, 80)
  rug.log('[checkpoint-probe] BEFORE B completed once')
  local result = rug.checkpoint.commit('panel-ready', '{"position":"after-panel-ready","ready":true}', '["panel-ready"]')
  assert(type(result) == 'table')
  rug.log('checkpoint: ' .. result.status .. ' / ' .. result.error)
  if result.status ~= 'committed' then return end
  rug.set_state('checkpoint_saved', true)
end
function on_restore(checkpointJson)
  assert(type(checkpointJson) == 'string' and checkpointJson:find('"nodeId":"panel-ready"', 1, true))
  rug.log('[checkpoint-probe] restored; BEFORE is not replayed; waiting 5s before AFTER C')
  rug.sleep(5000)
  rug.press_key(67, 80)
  rug.set_state('checkpoint_restored', true)
  rug.log('[checkpoint-probe] AFTER C completed once via on_restore')
end
function on_tick() rug.sleep(60000) end
function on_stop() rug.log('[checkpoint-probe] on_stop') end
