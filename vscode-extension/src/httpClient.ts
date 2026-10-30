import * as http from 'node:http';

export interface HttpResponse {
  status: number;
  body: string;
}

export function httpGet(url: string, timeoutMs: number): Promise<HttpResponse> {
  return new Promise((resolve, reject) => {
    const request = http.get(url, (response) => {
      let body = '';
      response.setEncoding('utf8');
      response.on('data', (chunk: string) => {
        body += chunk;
      });
      response.on('end', () => {
        resolve({ status: response.statusCode ?? 0, body });
      });
    });

    request.setTimeout(timeoutMs, () => {
      request.destroy(new Error('request timeout'));
    });
    request.on('error', reject);
  });
}

export async function httpGetJson<T>(
  url: string,
  timeoutMs: number,
): Promise<T | null> {
  try {
    const response = await httpGet(url, timeoutMs);
    if (response.status !== 200) {
      return null;
    }
    return JSON.parse(response.body) as T;
  } catch {
    return null;
  }
}

export function httpPost(
  url: string,
  timeoutMs: number,
  payload?: unknown,
): Promise<HttpResponse> {
  const body = payload === undefined ? undefined : JSON.stringify(payload);
  return new Promise((resolve, reject) => {
    const request = http.request(
      url,
      {
        method: 'POST',
        headers:
          body === undefined
            ? undefined
            : {
                'Content-Type': 'application/json',
                'Content-Length': Buffer.byteLength(body),
              },
      },
      (response) => {
        let responseBody = '';
        response.setEncoding('utf8');
        response.on('data', (chunk: string) => {
          responseBody += chunk;
        });
        response.on('end', () => {
          resolve({ status: response.statusCode ?? 0, body: responseBody });
        });
      },
    );

    request.setTimeout(timeoutMs, () => {
      request.destroy(new Error('request timeout'));
    });
    request.on('error', reject);
    request.end(body);
  });
}
