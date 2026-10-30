import * as vscode from 'vscode';

const language = vscode.env.language.toLowerCase();

export function tr(chinese: string, english: string): string {
  return language.startsWith('zh') ? chinese : english;
}
