function on_init()
  local result = rug.checkpoint.commit('panel-ready', '{"position":"after-panel-ready","ready":true}', '["panel-ready"]')
  assert(type(result) == 'table')
  rug.log('checkpoint: ' .. result.status .. ' / ' .. result.error)
  if result.status ~= 'committed' then return end
  rug.set_state('checkpoint_saved', true)
end
function on_tick() rug.sleep(60000) end
