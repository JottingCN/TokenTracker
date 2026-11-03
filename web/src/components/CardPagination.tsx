import { Pagination } from 'antd';

interface CardPaginationProps {
  current: number;
  pageSize: number;
  total: number;
  onChange: (page: number, pageSize: number) => void;
  showSizeChanger?: boolean;
}

export function CardPagination({
  current,
  pageSize,
  total,
  onChange,
  showSizeChanger = false,
}: CardPaginationProps) {
  if (total <= pageSize) {
    return null;
  }

  return (
    <Pagination
      size="small"
      current={current}
      pageSize={pageSize}
      total={total}
      showSizeChanger={showSizeChanger}
      pageSizeOptions={showSizeChanger ? [20, 50, 100] : undefined}
      onChange={onChange}
    />
  );
}
