/**
 * Optional event validation using JSON schemas from contracts/.
 * Only active when NODE_ENV !== 'production'.
 * Logs warnings for invalid events — never rejects them.
 */
const fs = require('fs');
const path = require('path');

const DEV = process.env.NODE_ENV !== 'production';

let validators = null;

function loadValidators() {
  if (validators) return validators;
  validators = {};

  if (!DEV) return validators;

  try {
    const Ajv = require('ajv');
    const ajv = new Ajv({ allErrors: true, strict: false, validateSchema: false });

    // Docker layout: /app/contracts; repo layout: ../../contracts
    const contractsDir = [
      path.join(__dirname, '..', 'contracts'),
      path.join(__dirname, '..', '..', 'contracts'),
    ].find(d => fs.existsSync(d));
    if (!contractsDir) {
      console.warn('[contracts] contracts/ directory not found — validation disabled');
      return validators;
    }

    // Load common definitions first
    const commonPath = path.join(contractsDir, 'common.json');
    if (fs.existsSync(commonPath)) {
      ajv.addSchema(JSON.parse(fs.readFileSync(commonPath, 'utf8')));
    }

    // Load event schemas
    const files = fs.readdirSync(contractsDir).filter(f => f.endsWith('.json') && f !== 'common.json');
    for (const file of files) {
      const schema = JSON.parse(fs.readFileSync(path.join(contractsDir, file), 'utf8'));
      const eventType = schema.title;
      if (eventType) {
        validators[eventType] = ajv.compile(schema);
      }
    }

    console.log(`[contracts] Loaded ${Object.keys(validators).length} event schemas (dev mode)`);
  } catch (err) {
    console.warn('[contracts] Failed to load schemas:', err.message);
  }

  return validators;
}

/**
 * Validate an event against its schema (dev mode only).
 * Returns true if valid or no schema exists. Logs warnings on failure.
 */
function validateEvent(event) {
  if (!DEV) return true;

  const v = loadValidators();
  const validate = v[event.event_type];
  if (!validate) return true;

  const valid = validate(event);
  if (!valid) {
    console.warn(
      `[contracts] ${event.event_type} validation failed:`,
      validate.errors.map(e => `${e.instancePath || '/'} ${e.message}`).join('; ')
    );
  }
  return valid;
}

module.exports = { validateEvent };
