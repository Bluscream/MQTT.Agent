import logging
import aiohttp
import voluptuous as vol
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant, ServiceCall
from homeassistant.helpers import config_validation as cv, device_registry as dr
from homeassistant.helpers.typing import ConfigType

from .const import DOMAIN, CONF_HOST, CONF_PORT, CONF_TOKEN, SERVICE_NOTIFY, SERVICE_START_PROCESS

_LOGGER = logging.getLogger(__name__)

NOTIFY_SCHEMA = vol.Schema({
    vol.Required("message"): cv.string,
    vol.Optional("title", default="Home Assistant"): cv.string,
    vol.Optional("type", default="toast"): cv.string,
    vol.Optional("msgbox_type", default="MB_OK"): cv.string,
    vol.Optional("msgbox_icon", default="MB_ICONINFORMATION"): cv.string,
    vol.Optional("timeout", default=0): cv.positive_int,
})

START_PROCESS_SCHEMA = vol.Schema({
    vol.Required("executable"): cv.string,
    vol.Optional("arguments"): cv.string,
    vol.Optional("as_user"): cv.string,
    vol.Optional("elevated", default=False): cv.boolean,
})

async def async_setup(hass: HomeAssistant, config: ConfigType) -> bool:
    """Set up the MQTT.Agent integration."""
    hass.data.setdefault(DOMAIN, {})
    return True

async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Set up MQTT.Agent from a config entry."""
    hass.data[DOMAIN][entry.entry_id] = entry.data

    async def get_target_config(call: ServiceCall):
        # In a multi-device setup, we should determine which agent to call.
        # This is typically done via device_id or entity_id in the service call.
        # For now, we'll use the entry associated with this setup if only one exists,
        # or we could try to match based on device registry if targeted.
        return entry.data

    async def handle_notify(call: ServiceCall):
        config = await get_target_config(call)
        host = config[CONF_HOST]
        port = config[CONF_PORT]
        token = config[CONF_TOKEN]
        
        url = f"http://{host}:{port}/api/system/notify"
        payload = {
            "message": call.data["message"],
            "title": call.data["title"],
            "type": call.data["type"],
            "msgbox_type": call.data["msgbox_type"],
            "msgbox_icon": call.data["msgbox_icon"],
            "timeout": call.data["timeout"]
        }
        
        async with aiohttp.ClientSession() as session:
            try:
                headers = {"Authorization": f"Bearer {token}"}
                async with session.post(url, json=payload, headers=headers) as response:
                    if response.status != 200:
                        _LOGGER.error("Failed to send notification to %s: %s", host, await response.text())
            except Exception as e:
                _LOGGER.error("Error sending notification to %s: %s", host, str(e))

    async def handle_start_process(call: ServiceCall):
        config = await get_target_config(call)
        host = config[CONF_HOST]
        port = config[CONF_PORT]
        token = config[CONF_TOKEN]
        
        url = f"http://{host}:{port}/api/system/start-process"
        payload = {
            "executable": call.data["executable"],
            "arguments": call.data.get("arguments"),
            "as_user": call.data.get("as_user"),
            "elevated": call.data["elevated"]
        }
        
        async with aiohttp.ClientSession() as session:
            try:
                headers = {"Authorization": f"Bearer {token}"}
                async with session.post(url, json=payload, headers=headers) as response:
                    if response.status != 200:
                        _LOGGER.error("Failed to start process on %s: %s", host, await response.text())
            except Exception as e:
                _LOGGER.error("Error starting process on %s: %s", host, str(e))

    hass.services.async_register(DOMAIN, SERVICE_NOTIFY, handle_notify, schema=NOTIFY_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_START_PROCESS, handle_start_process, schema=START_PROCESS_SCHEMA)

    return True

async def async_unload_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Unload a config entry."""
    hass.data[DOMAIN].pop(entry.entry_id)
    return True
