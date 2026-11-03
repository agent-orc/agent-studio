export interface PasskeyOptions {
  challenge: string;
  rpId: string;
  userId?: string;
  username?: string;
}

export interface PasskeyAssertion {
  credentialId: string;
  clientDataJson: string;
  authenticatorData: string;
  signature: string;
}

export interface PasskeyRegistration {
  credentialId: string;
  clientDataJson: string;
  authenticatorData: string;
  name: string;
}

function decode(value: string): Uint8Array<ArrayBuffer> {
  const padded = value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '=');
  return Uint8Array.from(atob(padded), character => character.charCodeAt(0));
}

function encode(value: ArrayBuffer): string {
  return btoa(String.fromCharCode(...new Uint8Array(value))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export async function createPasskey(options: PasskeyOptions): Promise<PasskeyRegistration> {
  if (!window.PublicKeyCredential) throw new Error('This browser does not support passkeys.');
  const username = options.username ?? 'Studio user';
  const credential = await navigator.credentials.create({ publicKey: {
    challenge: decode(options.challenge),
    rp: { id: options.rpId, name: 'Agent Software Studio' },
    user: { id: new TextEncoder().encode(options.userId ?? username), name: username, displayName: username },
    pubKeyCredParams: [{ type: 'public-key', alg: -7 }],
    authenticatorSelection: { residentKey: 'required', userVerification: 'required' },
    attestation: 'none',
    timeout: 120000,
  } }) as PublicKeyCredential | null;
  if (!credential) throw new Error('Passkey setup was cancelled.');
  const response = credential.response as AuthenticatorAttestationResponse & { getAuthenticatorData(): ArrayBuffer };
  return {
    credentialId: encode(credential.rawId),
    clientDataJson: encode(response.clientDataJSON),
    authenticatorData: encode(response.getAuthenticatorData()),
    name: 'Passkey',
  };
}

export async function getPasskey(options: PasskeyOptions): Promise<PasskeyAssertion> {
  if (!window.PublicKeyCredential) throw new Error('This browser does not support passkeys.');
  const credential = await navigator.credentials.get({ publicKey: {
    challenge: decode(options.challenge), rpId: options.rpId,
    userVerification: 'required', timeout: 120000,
  } }) as PublicKeyCredential | null;
  if (!credential) throw new Error('Passkey verification was cancelled.');
  const response = credential.response as AuthenticatorAssertionResponse;
  return {
    credentialId: encode(credential.rawId),
    clientDataJson: encode(response.clientDataJSON),
    authenticatorData: encode(response.authenticatorData),
    signature: encode(response.signature),
  };
}
