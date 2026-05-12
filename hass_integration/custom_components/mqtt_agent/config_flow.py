import voluptuous as vol
from homeassistant import config_entries
from homeassistant.core import callback
import aiohttp
import logging

from .const import DOMAIN, CONF_HOST, CONF_PORT, CONF_TOKEN, CONF_NAME

_LOGGER = logging.getLogger(__name__)

class MqttAgentConfigFlow(config_entries.ConfigFlow, domain=DOMAIN):
    """Handle a config flow for MQTT.Agent."""

    VERSION = 1

    async def async_step_user(self, user_input=None):
        """Handle the initial step."""
        errors = {}
        if user_input is not None:
            # Validate connection
            host = user_input[CONF_HOST]
            port = user_input[CONF_PORT]
            token = user_input[CONF_TOKEN]
            
            url = f"http://{host}:{port}/"
            headers = {"Authorization": f"Bearer {token}"}
            
            async with aiohttp.ClientSession() as session:
                try:
                    async with session.get(url, headers=headers, timeout=5) as response:
                        if response.status == 200:
                            return self.async_create_entry(
                                title=user_input[CONF_NAME],
                                data=user_input
                            )
                        else:
                            errors["base"] = "invalid_auth"
                except Exception:
                    errors["base"] = "cannot_connect"

        return self.async_show_form(
            step_id="user",
            data_schema=vol.Schema({
                vol.Required(CONF_NAME, default="Windows PC"): str,
                vol.Required(CONF_HOST): str,
                vol.Required(CONF_PORT, default=23482): int,
                vol.Required(CONF_TOKEN): str,
            }),
            errors=errors,
        )

    @staticmethod
    @callback
    def async_get_options_flow(config_entry):
        """Get the options flow for this handler."""
        return MqttAgentOptionsFlowHandler(config_entry)

class MqttAgentOptionsFlowHandler(config_entries.OptionsFlow):
    """Handle options flow for MQTT.Agent."""

    def __init__(self, config_entry):
        """Initialize options flow."""
        self.config_entry = config_entry

    async def async_step_init(self, user_input=None):
        """Manage the options."""
        if user_input is not None:
            return self.async_create_entry(title="", data=user_input)

        return self.async_show_form(
            step_id="init",
            data_schema=vol.Schema({
                vol.Required(CONF_HOST, default=self.config_entry.data.get(CONF_HOST)): str,
                vol.Required(CONF_PORT, default=self.config_entry.data.get(CONF_PORT)): int,
                vol.Required(CONF_TOKEN, default=self.config_entry.data.get(CONF_TOKEN)): str,
            }),
        )
