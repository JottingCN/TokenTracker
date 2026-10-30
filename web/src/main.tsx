import ReactDOM from 'react-dom/client';
import { App as AntApp, ConfigProvider, theme } from 'antd';
import App from './App';
import { MoneyProvider } from './utils/money';
import { UiProvider, useUi } from './ui';
import './index.css';

function ThemedRoot() {
  const { antdLocale, isDark, lang } = useUi();

  return (
    <ConfigProvider
      locale={antdLocale}
      theme={{
        algorithm: isDark ? theme.darkAlgorithm : theme.defaultAlgorithm,
        cssVar: true,
      }}
    >
      <AntApp>
        <MoneyProvider language={lang}>
          <App />
        </MoneyProvider>
      </AntApp>
    </ConfigProvider>
  );
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <UiProvider>
    <ThemedRoot />
  </UiProvider>,
);
