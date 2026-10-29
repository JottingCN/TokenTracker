import ReactDOM from 'react-dom/client';
import { App as AntApp, ConfigProvider } from 'antd';
import zhCN from 'antd/locale/zh_CN';
import App from './App';
import { MoneyProvider } from './utils/money';
import './index.css';

ReactDOM.createRoot(document.getElementById('root')!).render(
  <ConfigProvider locale={zhCN}>
    <AntApp>
      <MoneyProvider>
        <App />
      </MoneyProvider>
    </AntApp>
  </ConfigProvider>,
);
