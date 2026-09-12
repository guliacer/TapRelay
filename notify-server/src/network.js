import os from "node:os";

export function getLanIPv4Addresses() {
  const addresses = [];
  const interfaces = os.networkInterfaces();

  for (const infos of Object.values(interfaces)) {
    for (const info of infos ?? []) {
      if (info && info.family === "IPv4" && !info.internal) {
        addresses.push(info.address);
      }
    }
  }

  return [...new Set(addresses)];
}

export function formatAddressList(addresses, port) {
  if (addresses.length === 0) {
    return `no LAN IPv4 address found, use ws://127.0.0.1:${port}`;
  }
  return addresses.map((address) => `ws://${address}:${port}`).join(", ");
}
