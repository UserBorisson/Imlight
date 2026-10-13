-- Run after the old trainer closes and the replacement NPC options are installed.
-- NPCInteract is the native void event; SendEvent queues it on the client event loop.
function main()
    Log("TowerTraining: reopening native trainer after inventory refresh");
    SendEvent("NPCInteract");
end
