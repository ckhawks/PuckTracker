import geoip from "geoip-lite";

/**
 * Look up geographic info for an IP address.
 * Returns { country, region, city, lat, lon } or null if unknown.
 */
export function lookupIp(ipAddress) {
  const geo = geoip.lookup(ipAddress);
  if (!geo) return null;
  return {
    country: geo.country,
    region: geo.region,
    city: geo.city,
    lat: geo.ll?.[0] ?? null,
    lon: geo.ll?.[1] ?? null,
  };
}
