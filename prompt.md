i want you to take all the projects in @.references and combine them into one big MQTT.Agent project where everything that can be done directly via mqtt (use @.references\hass-win-status as most recent "best practices" example) will be done and features like screenshot grabbing are done via a local webserver with simple cmdline arg token auth like -token "token" and then Authorization: Bearer <token> or ?token=<token>

in the end, i want my homeassistant to have atleast
- a "<PC NAME>" mqtt device with entities like
   - select.<pcname>_status (like hass-win-status without --more-states)
   - a switch.<pcname>_block_shutdown switch for blocking shutdowns/logouts, etc with all kinds of techniques while its on
   - recreation of @.references\HassNotifyReciever so we can send notifications, banners via @.references\SoundSwitch.Banner or messageboxes or toasts from within homeassistant easily
- a way to get events in hass for more fine grained stuff like --more-states 
- actions for running commands elevated or as user like in @.references\ipc-mcp without a mcp server also the abilities to stop/start or restart any device in the device manager remotely and some usual stuff like shutdown, logoff, lock, reboot, etc, etc and the ability to log in a user from the logon screen like @.references\ipc-mcp offers

it should have the same exe for all parts and behave differently depending on which is invoked via cmdline args (-service/--service/"/service", -tray,--tray,"/tray") 

you can always refer back to @prompt.md so you dont forget anything