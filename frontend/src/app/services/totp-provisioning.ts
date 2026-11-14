/** Build the standard local authenticator handoff without sending the secret to a remote QR service.
 * Callers may trust this URL for Angular binding because each dynamic part is encoded here.
 */
export function totpProvisioningUri(secret: string, username: string): string {
  const label = encodeURIComponent(`Agent Software Studio:${username}`);
  const issuer = encodeURIComponent('Agent Software Studio');
  return `otpauth://totp/${label}?secret=${encodeURIComponent(secret)}&issuer=${issuer}&algorithm=SHA1&digits=6&period=30`;
}

export async function totpQrDataUrl(uri: string): Promise<string> {
  const { default: QRCode } = await import('qrcode');
  return QRCode.toDataURL(uri, { width: 192, margin: 2, errorCorrectionLevel: 'M' });
}
