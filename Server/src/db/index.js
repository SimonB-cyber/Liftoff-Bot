// Re-export all database modules for backward compatibility.
// Consumers can require('./db') or require('./database') interchangeably.
module.exports = {
  ...require('./connection'),
  ...require('./ingest'),
  ...require('./queries'),
  ...require('./chatTemplates'),
  ...require('./playlists'),
};
