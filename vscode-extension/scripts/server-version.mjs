export function readServerVersion(programSource) {
  const match = /new ServerInfoPayload\(\s*"TokenTracker\.Server",\s*"(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)"/
    .exec(programSource);
  if (!match) {
    throw new Error('Unable to read server version from Program.cs');
  }

  return match[1];
}
